using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace OpenVSA.Demod.Chain
{
    /// <summary>
    /// The order the demodulation steps are applied in, declared once (<c>REQ-DEM-001</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The reason this exists at all.</strong> Fourteen steps, three of them optional and
    /// one of them a loop, is more order than anyone holds in their head. Every step is specified
    /// somewhere else — the filters in <c>REQ-DEM-020</c>, the equaliser in <c>REQ-DEM-050</c>, the
    /// metrics in <c>REQ-DEM-060</c> — and none of those requirements says what runs before it.
    /// Without a stated order two developers will reasonably write equalise-then-refine and
    /// refine-then-equalise, and the two give different EVM on the same signal.
    /// </para>
    /// <para>
    /// <strong>Declared once means once.</strong> <see cref="Steps"/> is derived from
    /// <see cref="DemodStep"/>'s own values; <see cref="Render"/> writes the chain out in the form
    /// the specification and the user help both carry; and <see cref="Demodulator"/> executes by
    /// walking <see cref="Steps"/> rather than by calling fourteen methods in a row. There is no
    /// second copy of the order in this repository that is not generated from this one, and the
    /// tests compare the specification's text and the help topic's text against
    /// <see cref="Render"/> so that none can appear.
    /// </para>
    /// <para>
    /// <strong>Why the optional steps are named here and not left to settings.</strong> Which steps
    /// may be skipped is a property of the chain, not of a particular measurement: skipping the
    /// measurement filter would not be an option, it would be a different instrument. A settings
    /// object says whether an optional step is wanted this time;
    /// <see cref="IsOptional(DemodStep)"/> says whether it was ever allowed to be.
    /// </para>
    /// </remarks>
    public static class ProcessingOrder
    {
        /// <summary>
        /// The step the equaliser re-enters the chain at when it updates its coefficients.
        /// </summary>
        /// <remarks>
        /// The specification writes this as "re-enters at 8 on update", and it is the one backward
        /// movement the chain is allowed. <see cref="ChainJournal"/> permits a pass to begin here
        /// and nowhere else, so a step that quietly re-ran an earlier one would be caught rather
        /// than absorbed into the loop this constant legitimises.
        /// </remarks>
        public const DemodStep ReEntryPoint = DemodStep.JointRefinement;

        private static readonly ReadOnlyCollection<DemodStep> Order =
            new ReadOnlyCollection<DemodStep>(Sorted());

        private static readonly Dictionary<DemodStep, string> Titles =
            new Dictionary<DemodStep, string>
            {
                { DemodStep.SearchWindow, "Extract Search Length window from Main Time" },
                { DemodStep.BurstSearch, "Burst / pulse search" },
                { DemodStep.CoarseCarrier, "Coarse carrier estimate" },
                { DemodStep.Resample, "Resample to N points/symbol" },
                { DemodStep.MeasurementFilter, "Measurement (matched) filter" },
                { DemodStep.SyncSearch, "Sync-pattern search" },
                { DemodStep.ResultWindow, "Position Result Length window" },
                {
                    DemodStep.JointRefinement,
                    "Joint refinement, iterated to convergence: carrier frequency · carrier phase " +
                    "· symbol timing · amplitude"
                },
                { DemodStep.SymbolDecisions, "Symbol decisions → detected bits" },
                {
                    DemodStep.ReferenceRegeneration,
                    "Reference regeneration: bits → ideal symbols → reference filter → ideal waveform"
                },
                { DemodStep.Equaliser, "Adaptive equaliser" },
                {
                    DemodStep.ImpairmentEstimation,
                    "Impairment estimation: IQ offset, gain imbalance, quadrature skew, amplitude droop"
                },
                { DemodStep.ErrorMetrics, "Error metric computation at symbol instants" },
                { DemodStep.ResultTraces, "Result trace generation" },
            };

        private static readonly HashSet<DemodStep> Optional =
            new HashSet<DemodStep>
            {
                DemodStep.BurstSearch,
                DemodStep.SyncSearch,
                DemodStep.Equaliser,
            };

        /// <summary>The steps, in the order they are applied.</summary>
        /// <remarks>
        /// Derived from <see cref="DemodStep"/>'s own values rather than listed again here. A
        /// second list is a second thing to keep in step, and the whole point of this class is that
        /// there is one.
        /// </remarks>
        public static IReadOnlyList<DemodStep> Steps => Order;

        /// <summary>Where a step sits in the order, counting from zero.</summary>
        /// <param name="step">The step.</param>
        /// <returns>Its zero-based position.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Not a known step.</exception>
        public static int PositionOf(DemodStep step)
        {
            int position = Order.IndexOf(step);

            if (position < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(step), step, "Not a known demodulation step.");
            }

            return position;
        }

        /// <summary>The step's number as the specification and the help write it, from one.</summary>
        /// <param name="step">The step.</param>
        /// <returns>Its one-based number.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Not a known step.</exception>
        public static int NumberOf(DemodStep step) => PositionOf(step) + 1;

        /// <summary>Whether a step may be skipped.</summary>
        /// <param name="step">The step.</param>
        /// <returns><c>true</c> for the three the specification marks optional.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Not a known step.</exception>
        public static bool IsOptional(DemodStep step)
        {
            PositionOf(step);

            return Optional.Contains(step);
        }

        /// <summary>Whether one step is applied after another.</summary>
        /// <param name="step">The step in question.</param>
        /// <param name="other">The step to compare against.</param>
        /// <returns><c>true</c> when <paramref name="step"/> comes later in the order.</returns>
        /// <exception cref="ArgumentOutOfRangeException">A step is not known.</exception>
        public static bool IsAfter(DemodStep step, DemodStep other) =>
            PositionOf(step) > PositionOf(other);

        /// <summary>The step's title, without its optional or re-entry annotation.</summary>
        /// <param name="step">The step.</param>
        /// <returns>The title, as the specification words it.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Not a known step.</exception>
        public static string TitleOf(DemodStep step)
        {
            PositionOf(step);

            return Titles[step];
        }

        /// <summary>
        /// The step's title with the annotation the specification gives it.
        /// </summary>
        /// <param name="step">The step.</param>
        /// <returns>
        /// The title, followed by <c>(optional)</c> for a skippable step and by the re-entry note
        /// for the equaliser.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">Not a known step.</exception>
        public static string Describe(DemodStep step)
        {
            string title = TitleOf(step);

            if (step == DemodStep.Equaliser)
            {
                return title + " (optional; re-enters at " +
                    NumberOf(ReEntryPoint).ToString(CultureInfo.InvariantCulture) + " on update)";
            }

            return IsOptional(step) ? title + " (optional)" : title;
        }

        /// <summary>
        /// The whole chain, one numbered line per step.
        /// </summary>
        /// <returns>Fourteen lines, in order, each <c>"n. description"</c>.</returns>
        /// <remarks>
        /// This is the form the requirements document and the user help both carry, and the tests
        /// compare both of them against it. Rendering rather than transcribing is what makes
        /// <c>REQ-DEM-001</c>'s "the two cannot drift" true rather than merely intended.
        /// </remarks>
        public static IReadOnlyList<string> Render()
        {
            var lines = new List<string>(Order.Count);

            foreach (DemodStep step in Order)
            {
                lines.Add(
                    NumberOf(step).ToString(CultureInfo.InvariantCulture) + ". " + Describe(step));
            }

            return new ReadOnlyCollection<string>(lines);
        }

        private static DemodStep[] Sorted()
        {
            var steps = (DemodStep[])Enum.GetValues(typeof(DemodStep));

            Array.Sort(steps, (a, b) => ((int)a).CompareTo((int)b));

            return steps;
        }
    }
}
