using System;
using System.Collections.Generic;

namespace OpenVSA.PerformanceGate
{
    /// <summary>What the gate concluded about one target.</summary>
    public enum Verdict
    {
        /// <summary>Within the threshold, and the run could resolve the threshold.</summary>
        Passed = 0,

        /// <summary>Worse than the threshold by more than the run's own noise.</summary>
        Regressed,

        /// <summary>
        /// Too noisy to tell. Reported rather than passed, because a run that cannot resolve
        /// 15 % has not shown that there was no 15 % regression.
        /// </summary>
        Inconclusive,

        /// <summary>Measured, but there is nothing stored for this machine class to compare with.</summary>
        NoBaseline,

        /// <summary>The feature does not exist yet, and the target says which phase brings it.</summary>
        AwaitingPhase,

        /// <summary>
        /// The feature exists and no measurement arrived. A silent skip, which fails the run.
        /// </summary>
        Missing,
    }

    /// <summary>The gate's conclusion about one target.</summary>
    public sealed class TargetVerdict
    {
        internal TargetVerdict(
            PerformanceTarget target,
            Verdict verdict,
            TargetMeasurement measurement,
            BaselineEntry baseline,
            double relativeChange)
        {
            Target = target;
            Verdict = verdict;
            Measurement = measurement;
            Baseline = baseline;
            RelativeChange = relativeChange;
        }

        /// <summary>The target.</summary>
        public PerformanceTarget Target { get; }

        /// <summary>The conclusion.</summary>
        public Verdict Verdict { get; }

        /// <summary>What was measured, or <c>null</c> when nothing was.</summary>
        public TargetMeasurement Measurement { get; }

        /// <summary>What it was compared against, or <c>null</c>.</summary>
        public BaselineEntry Baseline { get; }

        /// <summary>
        /// How much worse the measurement is than the baseline, as a signed fraction: positive is
        /// a regression whichever direction the target counts as better.
        /// </summary>
        public double RelativeChange { get; }

        /// <summary>
        /// Whether the measurement misses the absolute figure the requirement states.
        /// </summary>
        /// <remarks>
        /// Distinct from a regression, and both are needed. The gate of <c>REQ-TST-007</c> compares
        /// against a baseline, so a target that has always been too slow passes it for ever: the
        /// baseline records what the product does, not what it is required to do. The stated figure
        /// is the requirement's own, and a run that misses it is reported however stable it is.
        /// </remarks>
        public bool MissesStatedTarget
        {
            get
            {
                if (Measurement == null)
                {
                    return false;
                }

                // AgainstStated, not Mean: for cold start the two are different populations, and
                // comparing the reproducible one against the requirement's figure reports a missed
                // requirement as met. See TargetMeasurement.AgainstStated.
                return Target.Better == Better.Higher
                    ? Measurement.AgainstStated < Target.Stated
                    : Measurement.AgainstStated > Target.Stated;
            }
        }

        /// <summary>Whether this verdict should fail the build.</summary>
        /// <remarks>
        /// A regression fails, obviously. <see cref="Verdict.Missing"/> fails too, and that is the
        /// less obvious half of <c>REQ-TST-007</c>: a harness that quietly shrinks to the targets
        /// that happen to be implemented reports success for a set nobody chose.
        /// </remarks>
        public bool Fails => Verdict == Verdict.Regressed || Verdict == Verdict.Missing;
    }

    /// <summary>
    /// Compares a run's measurements against the stored baselines (<c>REQ-TST-007</c>).
    /// </summary>
    /// <remarks>
    /// All the deciding, none of the measuring. Everything here is arithmetic over numbers that
    /// were recorded elsewhere, so the rules — the threshold, the noise floor, what a missing
    /// target means — are exercised by the unit suite in milliseconds rather than by running a
    /// benchmark and hoping the machine misbehaves in the right way.
    /// </remarks>
    public sealed class RegressionGate
    {
        /// <summary>The threshold the requirements state: worse by more than 15 % fails.</summary>
        public const double DefaultThreshold = 0.15;

        private readonly BaselineStore _baselines;

        /// <summary>Creates a gate over a baseline store.</summary>
        /// <param name="baselines">The stored figures.</param>
        /// <param name="threshold">
        /// The fractional regression that fails. Defaults to <see cref="DefaultThreshold"/>;
        /// parameterised only so a test can drive the boundary from both sides.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="baselines"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The threshold is not in (0, 1).</exception>
        public RegressionGate(BaselineStore baselines, double threshold = DefaultThreshold)
        {
            if (baselines == null)
            {
                throw new ArgumentNullException(nameof(baselines));
            }

            if (double.IsNaN(threshold) || threshold <= 0.0 || threshold >= 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(threshold), threshold, "A regression threshold must lie in (0, 1).");
            }

            _baselines = baselines;
            Threshold = threshold;
        }

        /// <summary>The fractional regression that fails the build.</summary>
        public double Threshold { get; }

        /// <summary>Judges a whole run.</summary>
        /// <param name="machine">The machine the run was taken on.</param>
        /// <param name="measurements">What the run measured; targets absent from it are examined.</param>
        /// <returns>One verdict per target in the catalogue, in requirement order.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public GateReport Judge(MachineClass machine, IEnumerable<TargetMeasurement> measurements)
        {
            if (machine == null)
            {
                throw new ArgumentNullException(nameof(machine));
            }

            if (measurements == null)
            {
                throw new ArgumentNullException(nameof(measurements));
            }

            var byName = new Dictionary<string, TargetMeasurement>(StringComparer.Ordinal);

            foreach (TargetMeasurement measurement in measurements)
            {
                byName[measurement.Name] = measurement;
            }

            bool recognised = _baselines.Recognises(machine);
            var verdicts = new List<TargetVerdict>();

            foreach (PerformanceTarget target in TargetCatalogue.All)
            {
                TargetMeasurement measured;
                byName.TryGetValue(target.Name, out measured);

                verdicts.Add(JudgeOne(target, measured, machine, recognised));
            }

            return new GateReport(machine, recognised, Threshold, verdicts);
        }

        private TargetVerdict JudgeOne(
            PerformanceTarget target,
            TargetMeasurement measured,
            MachineClass machine,
            bool recognised)
        {
            if (measured == null)
            {
                // A target whose feature is not built yet is expected to be absent, and says which
                // phase brings it. A target whose feature exists and produced no number was
                // skipped, and that is a failure rather than a gap.
                return new TargetVerdict(
                    target,
                    target.AwaitingPhase.HasValue ? Verdict.AwaitingPhase : Verdict.Missing,
                    null, null, double.NaN);
            }

            BaselineEntry baseline = recognised ? _baselines.Find(machine, target.Name) : null;

            if (baseline == null)
            {
                return new TargetVerdict(target, Verdict.NoBaseline, measured, null, double.NaN);
            }

            double change = RegressionOf(target, measured.Mean, baseline.Mean);
            double resolution = measured.RelativeResolution;

            // Worse than the threshold by more than the run's own noise: a real regression, and
            // saying so is safe even on a noisy machine.
            if (change - resolution > Threshold)
            {
                return new TargetVerdict(target, Verdict.Regressed, measured, baseline, change);
            }

            // Not clearly a regression — but if the run could not have resolved one, it has not
            // shown there wasn't one either.
            if (resolution > Threshold)
            {
                return new TargetVerdict(target, Verdict.Inconclusive, measured, baseline, change);
            }

            return new TargetVerdict(target, Verdict.Passed, measured, baseline, change);
        }

        /// <summary>
        /// How much worse a measurement is than a baseline, signed so positive is always worse.
        /// </summary>
        /// <param name="target">The target, which says which direction is better.</param>
        /// <param name="measured">The measured value.</param>
        /// <param name="baseline">The stored value.</param>
        /// <remarks>
        /// Both directions collapse to one number here so that every comparison downstream reads
        /// the same way. Getting this backwards for one target would let a halving of the update
        /// rate pass as an improvement, which no amount of threshold tuning would catch.
        /// </remarks>
        public static double RegressionOf(PerformanceTarget target, double measured, double baseline)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            if (baseline <= 0.0)
            {
                return double.NaN;
            }

            return target.Better == Better.Higher
                ? (baseline - measured) / baseline
                : (measured - baseline) / baseline;
        }
    }
}
