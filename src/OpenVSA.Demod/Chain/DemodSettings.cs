using System;
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
        /// The reference filter of step 10 has no type of its own yet: it is the full Nyquist pulse
        /// that a matched pair composes to, which is what the measured waveform is compared
        /// against. <c>REQ-DEM-020</c> requires both to be independently selectable in type, and
        /// that is where the second half arrives.
        /// </remarks>
        public PulseFilterType MeasurementFilter { get; set; } = PulseFilterType.RootRaisedCosine;

        /// <summary>The measurement filter's roll-off (<c>REQ-DEM-020</c>).</summary>
        public double MeasurementFilterAlpha { get; set; } = 0.35;

        /// <summary>The reference filter's roll-off (<c>REQ-DEM-020</c>).</summary>
        public double ReferenceFilterAlpha { get; set; } = 0.35;

        /// <summary>How many symbols either side of centre the filters span.</summary>
        public int FilterSymbolSpan { get; set; } = 6;

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
            Require(ResultLengthSymbols >= 4, "A Result Length of fewer than 4 symbols cannot be fitted to.");
            Require(SearchStartSample >= 0, "The Search Length window starts at or after the first sample.");
            Require(SearchLengthSamples >= 0, "A Search Length of zero means the rest of Main Time.");
            Require(FilterSymbolSpan >= 1, "A pulse spans at least one symbol either side of centre.");
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

        private static void Require(bool held, string what)
        {
            if (!held)
            {
                throw new ArgumentException(what);
            }
        }
    }
}
