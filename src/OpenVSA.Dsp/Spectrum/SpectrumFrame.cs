using System;
using OpenVSA.Core;
using OpenVSA.Dsp.Windowing;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// One computed spectrum, in dBm, with the axis and settings that produced it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Immutable, and that is the point.</strong> <c>REQ-NFR-011</c> requires trace results
    /// crossing into the UI thread to be snapshots no DSP thread can still be writing. The level
    /// array is owned exclusively by the frame and reachable only as a
    /// <see cref="ReadOnlySpan{T}"/>, so a consumer can neither mutate it nor store it past the
    /// frame — the same reasoning <c>REQ-DAT-001a</c> applies to <see cref="IqBlock"/>.
    /// </para>
    /// <para>
    /// <strong>One array per published frame.</strong> The buffer is not pooled, because pooling it
    /// would need a lifetime protocol saying when the UI has finished reading — and a frame that
    /// can be recycled while it is still on screen is precisely the torn frame the requirement
    /// forbids. The cost is bounded and known: one array of <see cref="PointCount"/> floats per
    /// update, which at the 2²⁰-point, 10 updates/s corner of <c>REQ-NFR-021</c> is 40 MB/s of
    /// gen-2 traffic. Recycling behind an explicit release is a later optimisation with a real
    /// invariant to state; guessing at it now would be the cheap kind of fast.
    /// </para>
    /// <para>
    /// Levels are in ascending frequency order — index 0 is <see cref="StartFrequencyHz"/> — not in
    /// the transform's natural order. Displaying raw FFT order is a spectrum split down the middle,
    /// so the shift belongs here rather than in every consumer.
    /// </para>
    /// </remarks>
    public sealed class SpectrumFrame
    {
        private readonly float[] _complex;
        private readonly AmplitudeScale _scale;
        private readonly object _gate = new object();

        private float[] _levelsDbm;

        private SpectrumFrame(
            float[] complex,
            AmplitudeScale scale,
            double startFrequencyHz,
            double binWidthHz,
            double centerFrequencyHz,
            double sampleRateHz,
            bool isBaseband,
            WindowType window,
            double equivalentNoiseBandwidthBins,
            double referenceLevelDbm,
            long sequenceNumber,
            DateTime acquiredUtc,
            FrontEndId source)
        {
            _complex = complex;
            _scale = scale;

            // The point count, until a producer that knows better says otherwise. A frame assembled
            // from stored levels has no transform behind it, and reporting zero there would make
            // TransformLength a value every consumer had to test before using.
            TransformLength = complex.Length / 2;
            StartFrequencyHz = startFrequencyHz;
            BinWidthHz = binWidthHz;
            CenterFrequencyHz = centerFrequencyHz;
            SampleRateHz = sampleRateHz;
            IsBaseband = isBaseband;
            Window = window;
            EquivalentNoiseBandwidthBins = equivalentNoiseBandwidthBins;
            ReferenceLevelDbm = referenceLevelDbm;
            SequenceNumber = sequenceNumber;
            AcquiredUtc = acquiredUtc;
            Source = source;
        }

        /// <summary>
        /// Wraps an array the caller must not retain, without copying it.
        /// </summary>
        /// <remarks>
        /// Internal because the no-retention rule cannot be enforced, only stated;
        /// <see cref="FromLevels"/> is the safe public route and copies. The producer is
        /// <see cref="SpectrumComputer"/>, which fills a fresh array per frame and drops it here.
        /// </remarks>
        internal static SpectrumFrame Adopt(
            float[] complex,
            AmplitudeScale scale,
            double startFrequencyHz,
            double binWidthHz,
            double centerFrequencyHz,
            double sampleRateHz,
            bool isBaseband,
            WindowType window,
            double equivalentNoiseBandwidthBins,
            double referenceLevelDbm,
            long sequenceNumber,
            DateTime acquiredUtc,
            FrontEndId source,
            int transformLength,
            bool transformWasCapped) =>
            new SpectrumFrame(
                complex, scale, startFrequencyHz, binWidthHz, centerFrequencyHz, sampleRateHz,
                isBaseband, window, equivalentNoiseBandwidthBins, referenceLevelDbm,
                sequenceNumber, acquiredUtc, source)
            {
                TransformLength = transformLength,
                TransformWasCapped = transformWasCapped,
            };

        /// <summary>
        /// Creates a frame from levels, copying them.
        /// </summary>
        /// <param name="levelsDbm">Levels in dBm, in ascending frequency order; must not be empty.</param>
        /// <param name="startFrequencyHz">Frequency of index 0, in hertz.</param>
        /// <param name="binWidthHz">Spacing between points, in hertz; must be positive.</param>
        /// <param name="window">Window used to compute them.</param>
        /// <param name="equivalentNoiseBandwidthBins">The window's ENBW in bins, for <see cref="ResolutionBandwidthHz"/>.</param>
        /// <returns>An immutable frame.</returns>
        /// <exception cref="ArgumentException"><paramref name="levelsDbm"/> is empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="binWidthHz"/> is not positive.</exception>
        /// <remarks>
        /// For tests and for consumers assembling a frame from stored data. A live measurement does
        /// not take this path, so the copy is not on the hot loop.
        /// </remarks>
        public static SpectrumFrame FromLevels(
            ReadOnlySpan<float> levelsDbm,
            double startFrequencyHz,
            double binWidthHz,
            WindowType window,
            double equivalentNoiseBandwidthBins)
        {
            if (levelsDbm.Length == 0)
            {
                throw new ArgumentException("A frame needs at least one point.", nameof(levelsDbm));
            }

            if (!(binWidthHz > 0.0) || double.IsInfinity(binWidthHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(binWidthHz), binWidthHz, "Bin width must be positive and finite.");
            }

            // Levels in, complex out: a level of L dBm becomes a real bin of the voltage that
            // produces it, so a frame built from stored levels behaves exactly like a measured one
            // and every format works from the same place.
            var scale = new AmplitudeScale(1.0, -10.0 * Math.Log10(2.0 * AmplitudeChain.DefaultReferenceImpedanceOhms) + 30.0);
            var complex = new float[levelsDbm.Length * 2];

            for (int i = 0; i < levelsDbm.Length; i++)
            {
                if (float.IsNaN(levelsDbm[i]))
                {
                    // Blanked in, blanked out. A gap is not a level of minus infinity.
                    complex[i * 2] = float.NaN;
                    complex[i * 2 + 1] = float.NaN;
                    continue;
                }

                double watts = Math.Pow(10.0, (levelsDbm[i] - 30.0) / 10.0);
                complex[i * 2] = (float)Math.Sqrt(2.0 * AmplitudeChain.DefaultReferenceImpedanceOhms * watts);
                complex[i * 2 + 1] = 0.0f;
            }

            double span = binWidthHz * (levelsDbm.Length - 1);

            return new SpectrumFrame(
                complex,
                scale,
                startFrequencyHz,
                binWidthHz,
                startFrequencyHz + span / 2.0,
                binWidthHz * levelsDbm.Length,
                isBaseband: false,
                window: window,
                equivalentNoiseBandwidthBins: equivalentNoiseBandwidthBins,
                referenceLevelDbm: 0.0,
                sequenceNumber: 0,
                acquiredUtc: DateTime.UtcNow,
                source: default(FrontEndId));
        }

        /// <summary>
        /// Creates a frame from calibrated complex values, copying them.
        /// </summary>
        /// <param name="complex">Interleaved real, imaginary values in volts peak; two per point.</param>
        /// <param name="startFrequencyHz">Frequency of point 0, in hertz.</param>
        /// <param name="binWidthHz">Spacing between points, in hertz; must be positive.</param>
        /// <param name="window">Window used to compute them.</param>
        /// <param name="equivalentNoiseBandwidthBins">The window's ENBW in bins.</param>
        /// <param name="referenceImpedanceOhms">Reference impedance for the decibel formats.</param>
        /// <returns>An immutable frame.</returns>
        /// <exception cref="ArgumentException"><paramref name="complex"/> is empty or has an odd length.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="binWidthHz"/> is not positive.</exception>
        /// <remarks>
        /// For a consumer holding a spectrum already in volts — stored data, or a test building one
        /// directly. Live measurement does not take this path, so the copy is not on the hot loop.
        /// </remarks>
        public static SpectrumFrame FromComplex(
            ReadOnlySpan<float> complex,
            double startFrequencyHz,
            double binWidthHz,
            WindowType window,
            double equivalentNoiseBandwidthBins,
            double referenceImpedanceOhms = AmplitudeChain.DefaultReferenceImpedanceOhms)
        {
            if (complex.Length == 0 || complex.Length % 2 != 0)
            {
                throw new ArgumentException(
                    "A complex spectrum needs two values per point and at least one point.",
                    nameof(complex));
            }

            if (!(binWidthHz > 0.0) || double.IsInfinity(binWidthHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(binWidthHz), binWidthHz, "Bin width must be positive and finite.");
            }

            var copy = new float[complex.Length];
            complex.CopyTo(new Span<float>(copy));

            int points = complex.Length / 2;
            double span = binWidthHz * (points - 1);

            // Values are already volts, so the linear half of the chain is unity and only the
            // impedance term remains.
            var scale = new AmplitudeScale(
                1.0, -10.0 * Math.Log10(2.0 * referenceImpedanceOhms) + 30.0);

            return new SpectrumFrame(
                copy,
                scale,
                startFrequencyHz,
                binWidthHz,
                startFrequencyHz + span / 2.0,
                binWidthHz * points,
                isBaseband: false,
                window: window,
                equivalentNoiseBandwidthBins: equivalentNoiseBandwidthBins,
                referenceLevelDbm: 0.0,
                sequenceNumber: 0,
                acquiredUtc: DateTime.UtcNow,
                source: default(FrontEndId));
        }

        /// <summary>
        /// The levels in dBm, ascending in frequency, in a view that cannot be written through.
        /// </summary>
        /// <remarks>
        /// Computed from the complex spectrum on first use and kept, so the common format costs
        /// nothing to ask for repeatedly and the others cost nothing when unused. The frame is
        /// immutable to its consumers either way — this is a cache, not a mutation.
        /// </remarks>
        public ReadOnlySpan<float> LevelsDbm
        {
            get
            {
                lock (_gate)
                {
                    if (_levelsDbm == null)
                    {
                        _levelsDbm = new float[PointCount];
                        TraceFormatter.Format(
                            new ReadOnlySpan<float>(_complex), TraceFormat.LogMagnitude,
                            _scale, BinWidthHz, new Span<float>(_levelsDbm));
                    }

                    return new ReadOnlySpan<float>(_levelsDbm);
                }
            }
        }

        /// <summary>
        /// The calibrated complex spectrum: interleaved real and imaginary, in volts peak.
        /// </summary>
        /// <remarks>
        /// What every format is derived from, and why changing format recomputes nothing
        /// (<c>REQ-TRC-001</c>). Referred to the instrument input with the amplitude chain of
        /// <c>REQ-AMP-001</c> already applied.
        /// </remarks>
        public ReadOnlySpan<float> Complex => new ReadOnlySpan<float>(_complex);

        /// <summary>The amplitude scale these values were calibrated with.</summary>
        public AmplitudeScale Scale => _scale;

        /// <summary>
        /// Whether this frame's complex values carry meaningful phase.
        /// </summary>
        /// <remarks>
        /// False after power-domain averaging, which squares the values and discards phase
        /// irrecoverably. <c>REQ-TRC-002</c> uses this to make the phase formats unselectable for
        /// such a trace rather than displaying a phase of zero as though it were measured.
        /// </remarks>
        public bool HasPhase { get; private set; } = true;

        /// <summary>Acquisitions this frame represents; 1 for an unaveraged one.</summary>
        public int AverageCount { get; private set; } = 1;

        /// <summary>
        /// The transform length this frame came from, in complex points.
        /// </summary>
        /// <remarks>
        /// Not the same as <see cref="PointCount"/>, which is what survived trimming to the
        /// analysis span. This is the number the resolution was actually bought with.
        /// </remarks>
        public int TransformLength { get; private set; }

        /// <summary>
        /// Whether the transform was bounded by <em>Max FFT Size</em> rather than by the record
        /// (<c>REQ-DSP-024</c>).
        /// </summary>
        /// <remarks>
        /// The requirement asks for the bound to be visible in the annotation, and the reason is
        /// that a capped measurement is not wrong — it is coarser than the samples could have made
        /// it. Nothing on the trace shows that: a spectrum measured at half the resolution it could
        /// have had looks entirely normal, which is the same reason <c>REQ-DSP-021</c> refuses an
        /// unreachable RBW instead of quietly clamping it.
        /// </remarks>
        public bool TransformWasCapped { get; private set; }

        /// <summary>
        /// Whether a characterised noise floor has been subtracted (<c>REQ-DSP-024</c>).
        /// </summary>
        public bool NoiseCorrected { get; private set; }

        /// <summary>
        /// Independent averages this frame is worth (<c>REQ-DSP-031</c>).
        /// </summary>
        /// <remarks>
        /// Equal to <see cref="AverageCount"/> when the acquisitions were independent, and strictly
        /// less when they were cut from one record with overlap — overlapping frames share samples,
        /// so they are correlated and reduce variance by less than their number. This is the figure
        /// a confidence statement about a noise measurement has to be made from; the raw count
        /// would overstate it by exactly what the overlap bought in time resolution.
        /// </remarks>
        public double EffectiveAverageCount { get; private set; } = 1.0;

        /// <summary>
        /// Returns a copy carrying different complex values, keeping this frame's axis.
        /// </summary>
        /// <param name="complex">Interleaved real, imaginary values in volts; ownership passes here.</param>
        /// <param name="hasPhase">Whether the new values carry phase.</param>
        /// <param name="averageCount">Acquisitions the new values represent.</param>
        /// <param name="effectiveAverageCount">Independent averages they are worth.</param>
        /// <exception cref="ArgumentNullException"><paramref name="complex"/> is null.</exception>
        /// <exception cref="ArgumentException">The length does not match this frame's point count.</exception>
        /// <remarks>
        /// For an averager, which produces new values over the same frequency axis. Internal
        /// because it takes ownership of the array, which cannot be enforced — the same reasoning
        /// as <see cref="Adopt"/>.
        /// </remarks>
        internal SpectrumFrame WithComplex(
            float[] complex, bool hasPhase, int averageCount, double effectiveAverageCount)
        {
            if (complex == null)
            {
                throw new ArgumentNullException(nameof(complex));
            }

            if (complex.Length != _complex.Length)
            {
                throw new ArgumentException(
                    "Expected " + _complex.Length + " values to match this frame's axis, got " +
                    complex.Length + ".",
                    nameof(complex));
            }

            return new SpectrumFrame(
                complex, _scale, StartFrequencyHz, BinWidthHz, CenterFrequencyHz, SampleRateHz,
                IsBaseband, Window, EquivalentNoiseBandwidthBins, ReferenceLevelDbm,
                SequenceNumber, AcquiredUtc, Source)
            {
                HasPhase = hasPhase,
                AverageCount = averageCount,
                EffectiveAverageCount = effectiveAverageCount,

                // Provenance, not values: how the trace was computed does not change because its
                // numbers did. A derived trace that forgot it had been capped would present the
                // resolution it inherited as the resolution it was entitled to.
                TransformLength = TransformLength,
                TransformWasCapped = TransformWasCapped,
                NoiseCorrected = NoiseCorrected,
            };
        }

        /// <summary>
        /// Returns a copy carrying different complex values and a noise-correction mark.
        /// </summary>
        /// <param name="complex">Interleaved real, imaginary values in volts; ownership passes here.</param>
        /// <param name="noiseCorrected">Whether a characterised noise floor has been subtracted.</param>
        /// <exception cref="ArgumentNullException"><paramref name="complex"/> is null.</exception>
        /// <exception cref="ArgumentException">The length does not match this frame's point count.</exception>
        internal SpectrumFrame WithNoiseCorrection(float[] complex, bool noiseCorrected)
        {
            // Subtracting an incoherent power leaves a magnitude, not a phasor: there is no phase
            // left to keep, and saying so is what stops the phase formats offering a zero as though
            // it had been measured.
            SpectrumFrame corrected = WithComplex(
                complex, false, AverageCount, EffectiveAverageCount);

            corrected.NoiseCorrected = noiseCorrected;

            return corrected;
        }

        /// <summary>Number of displayed frequency points.</summary>
        public int PointCount => _complex.Length / 2;

        /// <summary>
        /// Renders this frame in a display format, without recomputing the transform.
        /// </summary>
        /// <param name="format">The format to produce.</param>
        /// <param name="destination">
        /// Receives <c>PointCount × <see cref="TraceFormatter.ValuesPerPoint"/></c> values.
        /// </param>
        /// <exception cref="ArgumentException">The destination is the wrong length.</exception>
        public void Format(TraceFormat format, Span<float> destination) =>
            Format(format, destination, null);

        /// <summary>
        /// Renders this frame in a display format, with explicit phase and group-delay settings.
        /// </summary>
        /// <param name="format">The format to produce.</param>
        /// <param name="destination">
        /// Receives <c>PointCount × <see cref="TraceFormatter.ValuesPerPoint"/></c> values.
        /// </param>
        /// <param name="options">
        /// Aperture and jump tolerance (<c>REQ-DSP-044</c>, <c>REQ-DSP-045</c>), or <c>null</c> for
        /// the defaults.
        /// </param>
        /// <exception cref="ArgumentException">The destination is the wrong length.</exception>
        public void Format(TraceFormat format, Span<float> destination, TraceFormatOptions options) =>
            TraceFormatter.Format(
                new ReadOnlySpan<float>(_complex), format, _scale, BinWidthHz, destination, options);

        /// <summary>Frequency of point 0, in hertz.</summary>
        public double StartFrequencyHz { get; }

        /// <summary>Spacing between points, in hertz.</summary>
        public double BinWidthHz { get; }

        /// <summary>Frequency of the last point, in hertz.</summary>
        public double StopFrequencyHz => StartFrequencyHz + (PointCount - 1) * BinWidthHz;

        /// <summary>Centre frequency of the acquisition, in hertz.</summary>
        public double CenterFrequencyHz { get; }

        /// <summary>
        /// Width of the displayed axis, in hertz: <see cref="StopFrequencyHz"/> −
        /// <see cref="StartFrequencyHz"/>.
        /// </summary>
        /// <remarks>
        /// Point count minus one bins, not point count. The distinction is one bin out of several
        /// thousand and would be invisible — except that it is what makes the displayed span read
        /// as exactly the span the user asked for rather than a hair over it.
        /// </remarks>
        public double SpanHz => (PointCount - 1) * BinWidthHz;

        /// <summary>Sample rate of the acquisition, in hertz.</summary>
        public double SampleRateHz { get; }

        /// <summary>Whether this came from a real-baseband acquisition, and so is one-sided.</summary>
        public bool IsBaseband { get; }

        /// <summary>The analysis window.</summary>
        public WindowType Window { get; }

        /// <summary>The window's equivalent noise bandwidth, in bins.</summary>
        public double EquivalentNoiseBandwidthBins { get; }

        /// <summary>
        /// Resolution bandwidth, in hertz: <c>ENBW × bin width</c> (<c>REQ-DSP-020</c>).
        /// </summary>
        public double ResolutionBandwidthHz => EquivalentNoiseBandwidthBins * BinWidthHz;

        /// <summary>Reference level of the acquisition, in dBm.</summary>
        public double ReferenceLevelDbm { get; }

        /// <summary>Sequence number of the block this was computed from.</summary>
        public long SequenceNumber { get; }

        /// <summary>Acquisition timestamp of that block, UTC.</summary>
        public DateTime AcquiredUtc { get; }

        /// <summary>The front end that produced the block, for provenance only.</summary>
        public FrontEndId Source { get; }

        /// <summary>Frequency of a point, in hertz.</summary>
        /// <param name="index">Point index.</param>
        /// <exception cref="ArgumentOutOfRangeException">The index is outside the frame.</exception>
        public double FrequencyAt(int index)
        {
            if (index < 0 || index >= PointCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Outside the frame.");
            }

            return StartFrequencyHz + index * BinWidthHz;
        }

        /// <summary>
        /// Index of the highest point, or −1 for an empty frame.
        /// </summary>
        /// <remarks>
        /// Here rather than in the marker layer because it is the one search the annotation needs
        /// before markers exist (<c>REQ-MKR-001</c> owns the rest).
        /// </remarks>
        public int IndexOfPeak()
        {
            ReadOnlySpan<float> levels = LevelsDbm;
            int peak = -1;
            float highest = float.NegativeInfinity;

            for (int i = 0; i < levels.Length; i++)
            {
                if (levels[i] > highest)
                {
                    highest = levels[i];
                    peak = i;
                }
            }

            return peak;
        }
    }
}
