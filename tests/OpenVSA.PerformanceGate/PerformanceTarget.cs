using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpenVSA.PerformanceGate
{
    /// <summary>Which direction of change is a regression.</summary>
    public enum Better
    {
        /// <summary>More is better — updates per second, throughput.</summary>
        Higher = 0,

        /// <summary>Less is better — elapsed time, allocation.</summary>
        Lower,
    }

    /// <summary>
    /// One of the seven performance targets of <c>REQ-NFR-020</c>–<c>REQ-NFR-026</c>.
    /// </summary>
    /// <remarks>
    /// The seven share a single acceptance criterion — "an automated benchmark harness runs in CI
    /// and fails the build on a >15 % regression against a stored baseline" — which is why they are
    /// one catalogue and not seven unrelated numbers. None of them could be met until the harness
    /// existed, and for a long time none of them was.
    /// </remarks>
    public sealed class PerformanceTarget
    {
        /// <summary>Creates a target.</summary>
        /// <param name="requirement">The requirement ID, e.g. <c>REQ-NFR-020</c>.</param>
        /// <param name="name">The benchmark name that measures it.</param>
        /// <param name="description">What is being measured, as the requirement states it.</param>
        /// <param name="unit">The unit of the measured value.</param>
        /// <param name="better">Which direction is an improvement.</param>
        /// <param name="stated">The absolute figure the requirement states.</param>
        /// <param name="awaitingPhase">
        /// The delivery phase that must land before this can be measured at all, or <c>null</c>
        /// when the feature exists now.
        /// </param>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        public PerformanceTarget(
            string requirement,
            string name,
            string description,
            string unit,
            Better better,
            double stated,
            int? awaitingPhase)
        {
            if (requirement == null)
            {
                throw new ArgumentNullException(nameof(requirement));
            }

            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            Requirement = requirement;
            Name = name;
            Description = description ?? string.Empty;
            Unit = unit ?? string.Empty;
            Better = better;
            Stated = stated;
            AwaitingPhase = awaitingPhase;
        }

        /// <summary>The requirement this target belongs to.</summary>
        public string Requirement { get; }

        /// <summary>The benchmark whose result is compared against the baseline.</summary>
        public string Name { get; }

        /// <summary>What is measured, in the requirement's own words.</summary>
        public string Description { get; }

        /// <summary>The unit of the measured value.</summary>
        public string Unit { get; }

        /// <summary>Which direction of change counts as an improvement.</summary>
        public Better Better { get; }

        /// <summary>The absolute figure the requirement states for the reference machine.</summary>
        public double Stated { get; }

        /// <summary>
        /// The phase that must deliver before this target can be measured, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// Stated rather than inferred from a missing measurement. "No result for this target"
        /// and "this target's feature does not exist yet" are different facts, and a harness that
        /// conflated them would quietly shrink to whatever happened to be implemented — which is
        /// the failure <c>REQ-TST-007</c> names explicitly.
        /// </remarks>
        public int? AwaitingPhase { get; }

        /// <inheritdoc />
        public override string ToString() => Requirement + " (" + Name + ")";
    }

    /// <summary>The seven targets, as the specification states them.</summary>
    public static class TargetCatalogue
    {
        private static readonly ReadOnlyCollection<PerformanceTarget> Targets =
            new ReadOnlyCollection<PerformanceTarget>(new[]
            {
                new PerformanceTarget(
                    "REQ-NFR-020", "Spectrum8192Rendered",
                    "Spectrum, 8 192-point FFT, single trace, rendered",
                    "updates/s", Better.Higher, 60.0, null),

                new PerformanceTarget(
                    "REQ-NFR-021", "Spectrum1MRenderedDecimated",
                    "Spectrum, 1 048 576-point FFT, rendered with min/max decimation",
                    "updates/s", Better.Higher, 10.0, null),

                new PerformanceTarget(
                    "REQ-NFR-022", "Demod16Qam4096Symbols",
                    "Flexible demod, 16-QAM, 4 096 symbols, 4 pts/symbol, equaliser off",
                    "ms", Better.Lower, 50.0, 2),

                new PerformanceTarget(
                    "REQ-NFR-023", "Demod1024Qam4000SymbolsEqualised",
                    "Flexible demod, 1024-QAM, 4 000 symbols, equaliser on (31 symbols)",
                    "ms", Better.Lower, 400.0, 2),

                new PerformanceTarget(
                    "REQ-NFR-024", "TwentyTraceWindows",
                    "20 simultaneous trace windows updating",
                    "updates/s", Better.Higher, 10.0, null),

                new PerformanceTarget(
                    "REQ-NFR-025", "ColdStartToFirstTrace",
                    "Cold start to first trace displayed, simulated source",
                    "s", Better.Lower, 3.0, null),

                new PerformanceTarget(
                    "REQ-NFR-026", "PlaybackFourGigabyteRecording",
                    "Playback of a 4 GB recording, sustained, at the recorded sample rate",
                    "x real-time", Better.Higher, 1.0, 3),
            });

        /// <summary>All seven targets, in requirement order.</summary>
        public static IReadOnlyList<PerformanceTarget> All => Targets;

        /// <summary>The target with a given benchmark name, or <c>null</c>.</summary>
        /// <param name="name">The benchmark name, compared exactly.</param>
        public static PerformanceTarget ByName(string name)
        {
            foreach (PerformanceTarget target in Targets)
            {
                if (string.Equals(target.Name, name, StringComparison.Ordinal))
                {
                    return target;
                }
            }

            return null;
        }
    }
}
