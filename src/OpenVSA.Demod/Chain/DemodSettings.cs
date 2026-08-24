using System;
using System.Globalization;
using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Chain
{
    /// <summary>
    /// What one demodulation was asked for: the signal's parameters, the optional steps that are
    /// wanted, and the bounds the iterations are held to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The symbol rate is supplied, never estimated.</strong> That is <c>REQ-DEM-030</c>'s
    /// rule, and it is why <see cref="SymbolRateHz"/> has no default: a demodulator that guessed
    /// would sometimes guess well, and the failures would look like a bad signal rather than like a
    /// missing setting.
    /// </para>
    /// <para>
    /// <strong>Which optional steps run is a setting; which steps may be optional is not.</strong>
    /// <see cref="IsEnabled"/> answers only for the three
    /// <see cref="ProcessingOrder.IsOptional(DemodStep)"/> allows, and throws for the rest rather
    /// than returning a polite <c>true</c> — a caller asking whether the measurement filter is
    /// enabled has a misunderstanding worth surfacing.
    /// </para>
    /// </remarks>
    public sealed class DemodSettings
    {
        /// <summary>The default internal processing rate, in samples per symbol.</summary>
        /// <remarks>
        /// Four is enough for the matched filter to be applied without aliasing at any roll-off,
        /// and for the timing interpolation of step 8 to have something to interpolate between.
        /// <c>REQ-DEM-034</c> makes the <em>displayed</em> points per symbol a separate setting,
        /// and <c>REQ-DEM-034a</c> requires that this rate not follow it.
        /// </remarks>
        public const int DefaultPointsPerSymbol = 4;

        /// <summary>The default bound on step 8's iterations.</summary>
        public const int DefaultMaxRefinementIterations = 20;

        /// <summary>The default bound on the number of passes over the chain.</summary>
        /// <remarks>
        /// Three: the first pass, and two chances for the equaliser to improve on it. The bound
        /// exists because an equaliser that keeps finding a reason to update would otherwise loop
        /// for as long as a measurement is running, and <c>REQ-DEM-001</c> wants the bound reported
        /// rather than reached in silence.
        /// </remarks>
        public const int DefaultMaxPasses = 3;

        /// <summary>
        /// The default filter span, in symbols either side of centre (<c>REQ-DEM-023</c>).
        /// </summary>
        /// <remarks>
        /// Eight: the least that requirement recommends for a root raised cosine. See
        /// <see cref="FilterSymbolSpan"/> for the measured trade this is the informed end of.
        /// </remarks>
        public const int DefaultFilterSymbolSpan = 8;

        private Constellation _constellation = Constellation.Qpsk();

        /// <summary>What the symbols are decided against.</summary>
        /// <exception cref="ArgumentNullException">Set to null.</exception>
        public Constellation Constellation
        {
            get => _constellation;
            set => _constellation = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>The symbol rate, in hertz. Supplied, per <c>REQ-DEM-030</c>.</summary>
        public double SymbolRateHz { get; set; }

        /// <summary>The internal processing rate, in samples per symbol.</summary>
        public int PointsPerSymbol { get; set; } = DefaultPointsPerSymbol;

        /// <summary>
        /// Which bits the constellation's points carry (<c>REQ-DEM-011</c>).
        /// </summary>
        /// <remarks>
        /// A property of <see cref="Constellation"/> rather than a setting of its own, because a
        /// labelling belongs to the format: <c>Constellation.WithMapping</c> is how it is chosen, and
        /// this is here so that a caller can read it back without reaching through.
        /// </remarks>
        public BitMapping Mapping => Constellation.Mapping;

        /// <summary>What a symbol's bits are read against (<c>REQ-DEM-012</c>).</summary>
        /// <remarks>
        /// Left at <see cref="DifferentialReference.PerFormat"/> the format decides, which is what a
        /// user selecting DQPSK from a menu means. The other two values are how the selection is
        /// shown to be effective: forcing <see cref="DifferentialReference.None"/> on a
        /// differentially encoded signal returns the encoded symbols instead of the data, which is
        /// wrong in a way that can be predicted and therefore tested.
        /// </remarks>
        public DifferentialReference DifferentialReference { get; set; } =
            DifferentialReference.PerFormat;

        /// <summary>
        /// Whether this measurement decodes differentially, once the format and the selection are
        /// both accounted for.
        /// </summary>
        public bool DecodesDifferentially =>
            DifferentialReference == DifferentialReference.PreviousSymbol ||
            (DifferentialReference == DifferentialReference.PerFormat &&
             Constellation.IsDifferential);

        /// <summary>
        /// How many instants of the waveform one symbol is read at: two for an offset format, one
        /// for every other (<c>REQ-DEM-012</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Not a setting, and deliberately: <c>REQ-DEM-012</c> makes it a property of the format,
        /// and <c>REQ-DEM-034a</c> requires that no display parameter change it. An offset format
        /// staggers I half a symbol from Q, so a chain that read one instant per symbol would read
        /// one of the two axes halfway between its symbols — where a pulse-shaped waveform is the
        /// average of two neighbours rather than either. The resulting EVM looks plausible, in the
        /// several-per-cent region, which is exactly why this is stated rather than left to whoever
        /// writes the next step.
        /// </para>
        /// <para>
        /// Two instants is also where the requirement's number comes from. At two points per symbol
        /// the second instant falls on the sample between two symbol instants — the whole reason
        /// that is the rate it names — and <see cref="Validate"/> therefore holds an offset format
        /// to an even internal rate. The interpolator would resolve an odd one, since the timing
        /// estimate is already fractional; requiring evenness keeps the second instant on the same
        /// grid as the first rather than permanently between two of its samples.
        /// </para>
        /// </remarks>
        public int InstantsPerSymbol => Constellation.IsOffset ? 2 : 1;

        /// <summary>Where the Search Length window starts in Main Time, in samples.</summary>
        public int SearchStartSample { get; set; }

        /// <summary>
        /// How long the Search Length window is, in samples; zero for the rest of Main Time.
        /// </summary>
        public int SearchLengthSamples { get; set; }

        /// <summary>How many symbols the Result Length window holds (<c>REQ-DEM-031</c>).</summary>
        public int ResultLengthSymbols { get; set; } = 256;

        /// <summary>Which measurement filter is applied at step 5 (<c>REQ-DEM-021</c>).</summary>
        /// <remarks>
        /// The receiver's half of the Nyquist pair: it must match the transmitter's shaping, so that
        /// the two compose to the full Nyquist filter and there is no intersymbol interference at
        /// the symbol centres. <c>REQ-DEM-020</c>'s rationale, and the reason this is chosen
        /// independently of <see cref="ReferenceFilter"/>.
        /// </remarks>
        public PulseFilterType MeasurementFilter { get; set; } = PulseFilterType.RootRaisedCosine;

        /// <summary>
        /// Which reference filter shapes the ideal waveform at step 10 (<c>REQ-DEM-020</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Defaults to the raised cosine, because the measured waveform has been through the
        /// transmitter's root raised cosine and step 5's matching half, and the composite of those
        /// two is a raised cosine. Shaping the reference with a root instead puts several per cent
        /// of EVM on a perfect signal: the two waveforms then differ in shape between the symbol
        /// instants even when every symbol is right.
        /// </para>
        /// <para>
        /// Independent of <see cref="MeasurementFilter"/> in type as well as in parameter, which is
        /// the half of <c>REQ-DEM-020</c> that arrived with <c>REQ-DEM-021</c>'s catalogue.
        /// </para>
        /// </remarks>
        public PulseFilterType ReferenceFilter { get; set; } = PulseFilterType.RaisedCosine;

        /// <summary>The measurement filter's roll-off (<c>REQ-DEM-020</c>).</summary>
        public double MeasurementFilterAlpha { get; set; } = PulseFilter.DefaultAlpha;

        /// <summary>The reference filter's roll-off (<c>REQ-DEM-020</c>).</summary>
        public double ReferenceFilterAlpha { get; set; } = PulseFilter.DefaultAlpha;

        /// <summary>The measurement filter's bandwidth–time product, for the Gaussian.</summary>
        public double MeasurementFilterBandwidthTime { get; set; } =
            PulseFilter.DefaultBandwidthTime;

        /// <summary>The reference filter's bandwidth–time product, for the Gaussian.</summary>
        public double ReferenceFilterBandwidthTime { get; set; } =
            PulseFilter.DefaultBandwidthTime;

        /// <summary>The measurement filter's cutoff, as a fraction of the symbol rate.</summary>
        public double MeasurementFilterCutoff { get; set; } = PulseFilter.DefaultCutoff;

        /// <summary>The reference filter's cutoff, as a fraction of the symbol rate.</summary>
        public double ReferenceFilterCutoff { get; set; } = PulseFilter.DefaultCutoff;

        /// <summary>The taps of a user-defined measurement filter, or <c>null</c>.</summary>
        public double[] MeasurementFilterTaps { get; set; }

        /// <summary>The taps of a user-defined reference filter, or <c>null</c>.</summary>
        public double[] ReferenceFilterTaps { get; set; }

        /// <summary>How many samples a symbol the user's taps were given at.</summary>
        public int UserFilterSamplesPerSymbol { get; set; } = DefaultPointsPerSymbol;

        /// <summary>
        /// How many symbols either side of centre the filters span (<c>REQ-DEM-023</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Eight, which is the least that requirement recommends for a root raised cosine
        /// and was six until 24 August 2026.</strong> The cost of the change is real — the taps, and
        /// the work per sample in step 5, grow with it — and so is what it buys. Measured on this
        /// chain at sixteen samples a symbol, transmit and receive spans matched:
        /// </para>
        /// <code>
        /// span  6  ->  0.287 %rms      span 12  ->  0.139 %rms
        /// span  8  ->  0.212 %rms      span 16  ->  0.098 %rms
        /// span 10  ->  0.273 %rms      span 20  ->  0.020 %rms
        /// </code>
        /// <para>
        /// The trade is not monotone — ten is worse than eight — because the tail of the pulse
        /// changes sign where it is cut, which is exactly why <c>REQ-DEM-023</c> asks for the curve
        /// to be in the user help rather than for a rule of thumb.
        /// </para>
        /// </remarks>
        public int FilterSymbolSpan { get; set; } = DefaultFilterSymbolSpan;

        /// <summary>The measurement filter, as the catalogue describes it.</summary>
        /// <remarks>
        /// Built on demand from the settings above rather than stored, so that a caller changing a
        /// roll-off does not have to remember to rebuild anything — and so that a state file, which
        /// holds a type and some numbers, needs no other representation.
        /// </remarks>
        public PulseFilter MeasurementPulse =>
            Pulse(
                MeasurementFilter,
                MeasurementFilterAlpha,
                MeasurementFilterBandwidthTime,
                MeasurementFilterCutoff,
                MeasurementFilterTaps);

        /// <summary>The reference filter, as the catalogue describes it.</summary>
        public PulseFilter ReferencePulse =>
            Pulse(
                ReferenceFilter,
                ReferenceFilterAlpha,
                ReferenceFilterBandwidthTime,
                ReferenceFilterCutoff,
                ReferenceFilterTaps);

        /// <summary>Whether step 2 runs.</summary>
        public bool BurstSearchEnabled { get; set; }

        /// <summary>Whether step 6 runs.</summary>
        public bool SyncSearchEnabled { get; set; }

        /// <summary>Whether step 11 runs.</summary>
        public bool EqualiserEnabled { get; set; }

        /// <summary>The symbol values step 6 looks for, when it runs.</summary>
        public int[] SyncPattern { get; set; }

        /// <summary>How many taps the equaliser has; an odd count.</summary>
        public int EqualiserTaps { get; set; } = 21;

        /// <summary>
        /// How much the equaliser must change the waveform for it to ask for a re-entry.
        /// </summary>
        /// <remarks>
        /// The energy of the change as a fraction of the waveform's own — so a thousandth is a
        /// change of about 3 % in amplitude somewhere in the block. Below this the equaliser has
        /// found nothing worth another pass over steps 8 to 14, and running one would produce the
        /// same numbers at the cost of the time it took.
        /// </remarks>
        public double EqualiserUpdateThreshold { get; set; } = 1e-3;

        /// <summary>The bound on step 8's iterations.</summary>
        public int MaxRefinementIterations { get; set; } = DefaultMaxRefinementIterations;

        /// <summary>
        /// The convergence criterion for step 8, as the largest parameter change that counts as
        /// converged.
        /// </summary>
        /// <remarks>
        /// Applied to the frequency change in cycles per symbol, the phase change in radians, the
        /// timing change in samples and the fractional gain change, so one number covers four
        /// parameters that are all dimensionless once expressed per symbol.
        /// </remarks>
        public double RefinementTolerance { get; set; } = 1e-6;

        /// <summary>The bound on the number of passes over the chain.</summary>
        public int MaxPasses { get; set; } = DefaultMaxPasses;

        /// <summary>Whether an optional step is wanted this time.</summary>
        /// <param name="step">One of the three optional steps.</param>
        /// <returns>Whether it runs.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The step is not optional, so there is no setting to answer with.
        /// </exception>
        public bool IsEnabled(DemodStep step)
        {
            switch (step)
            {
                case DemodStep.BurstSearch:
                    return BurstSearchEnabled;

                case DemodStep.SyncSearch:
                    return SyncSearchEnabled;

                case DemodStep.Equaliser:
                    return EqualiserEnabled;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(step),
                        step,
                        "Step " + ProcessingOrder.NumberOf(step) + " is not optional, so nothing " +
                        "decides whether it runs.");
            }
        }

        /// <summary>Checks the settings hold together, before anything is measured with them.</summary>
        /// <exception cref="ArgumentException">A setting is outside its range.</exception>
        public void Validate()
        {
            Require(SymbolRateHz > 0.0, "The symbol rate is supplied and positive (REQ-DEM-030).");
            Require(PointsPerSymbol >= 2, "The internal processing rate is at least 2 points per symbol.");

            Require(
                !Constellation.IsOffset || (PointsPerSymbol % 2) == 0,
                Constellation.Name + " staggers I and Q by half a symbol, so REQ-DEM-012 " +
                "demodulates it at two instants per symbol. An internal rate of " +
                PointsPerSymbol.ToString(CultureInfo.InvariantCulture) + " points per symbol " +
                "would put the second of them between two samples on every symbol; an even rate " +
                "puts it on one.");

            Require(
                !DecodesDifferentially || Constellation.IsIndexedRing,
                "A differential decode reads the change from one symbol to the next as a change " +
                "of phase, so it needs a constellation whose symbol values run around one ring. " +
                Constellation.Name + "'s do not, and subtracting two of them would give a " +
                "well-formed bit stream that meant nothing.");
            Require(ResultLengthSymbols >= 4, "A Result Length of fewer than 4 symbols cannot be fitted to.");
            Require(SearchStartSample >= 0, "The Search Length window starts at or after the first sample.");
            Require(SearchLengthSamples >= 0, "A Search Length of zero means the rest of Main Time.");
            Require(FilterSymbolSpan >= 1, "A pulse spans at least one symbol either side of centre.");

            Require(
                MeasurementFilterBandwidthTime > 0.0 && ReferenceFilterBandwidthTime > 0.0,
                "A bandwidth-time product is positive.");

            Require(
                MeasurementFilterCutoff > 0.0 && ReferenceFilterCutoff > 0.0,
                "A low-pass cutoff is a positive fraction of the symbol rate.");

            // Building them is the check: each factory refuses its own parameters, and a filter
            // that cannot be built is better refused here than at the first block of a measurement.
            Require(MeasurementPulse != null, "The measurement filter is one this build has.");
            Require(ReferencePulse != null, "The reference filter is one this build has.");
            Require(
                MeasurementFilterAlpha >= 0.0 && MeasurementFilterAlpha <= 1.0,
                "The measurement filter's roll-off runs from 0 to 1.");
            Require(
                ReferenceFilterAlpha >= 0.0 && ReferenceFilterAlpha <= 1.0,
                "The reference filter's roll-off runs from 0 to 1.");
            Require(EqualiserTaps >= 1 && (EqualiserTaps % 2) == 1, "The equaliser has an odd number of taps.");
            Require(EqualiserUpdateThreshold >= 0.0, "The equaliser's update threshold is not negative.");
            Require(MaxRefinementIterations >= 1, "Step 8 is allowed at least one iteration.");
            Require(RefinementTolerance > 0.0, "The convergence criterion is a positive tolerance.");
            Require(MaxPasses >= 1, "The chain runs at least one pass.");

            Require(
                !SyncSearchEnabled || (SyncPattern != null && SyncPattern.Length > 0),
                "Step 6 is enabled with no sync pattern to search for.");

            if (SyncPattern != null)
            {
                foreach (int symbol in SyncPattern)
                {
                    Require(
                        symbol >= 0 && symbol < Constellation.Count,
                        "The sync pattern names symbol " + symbol + ", which " +
                        Constellation.Name + " does not have.");
                }
            }
        }

        /// <summary>One filter from a type and the parameters that go with it.</summary>
        /// <param name="type">Which filter.</param>
        /// <param name="alpha">The roll-off, used by the raised-cosine pair.</param>
        /// <param name="bandwidthTime">The bandwidth–time product, used by the Gaussian.</param>
        /// <param name="cutoff">The cutoff, used by the low-pass.</param>
        /// <param name="taps">The taps, used by a user-defined filter.</param>
        /// <returns>The filter.</returns>
        /// <exception cref="ArgumentException">
        /// A user-defined filter was asked for with no taps.
        /// </exception>
        private PulseFilter Pulse(
            PulseFilterType type,
            double alpha,
            double bandwidthTime,
            double cutoff,
            double[] taps)
        {
            switch (type)
            {
                case PulseFilterType.RootRaisedCosine:
                    return PulseFilter.RootRaisedCosine(alpha);

                case PulseFilterType.RaisedCosine:
                    return PulseFilter.RaisedCosine(alpha);

                case PulseFilterType.Gaussian:
                    return PulseFilter.Gaussian(bandwidthTime);

                case PulseFilterType.Edge:
                    return PulseFilter.Edge();

                case PulseFilterType.HalfSine:
                    return PulseFilter.HalfSine();

                case PulseFilterType.Rectangular:
                    return PulseFilter.Rectangular();

                case PulseFilterType.LowPass:
                    return PulseFilter.LowPass(cutoff);

                case PulseFilterType.None:
                    return PulseFilter.None();

                case PulseFilterType.UserDefined:
                    if (taps == null || taps.Length == 0)
                    {
                        throw new ArgumentException(
                            "A user-defined filter was selected and no taps were given. There is " +
                            "no default shape for one: what it is, is the taps.");
                    }

                    return PulseFilter.UserDefined(taps, UserFilterSamplesPerSymbol);

                default:
                    throw new ArgumentException("No filter of type " + type + " is in the catalogue.");
            }
        }

        private static void Require(bool held, string what)
        {
            if (!held)
            {
                throw new ArgumentException(what);
            }
        }
    }
}
