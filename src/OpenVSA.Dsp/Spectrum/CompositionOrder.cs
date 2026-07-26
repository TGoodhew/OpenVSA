using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// The stages an analysis passes through, in the order they are applied
    /// (<c>REQ-TRC-003</c>).
    /// </summary>
    /// <remarks>
    /// <strong>Declaration order is application order.</strong> The values are numbered by their
    /// position in the pipeline, and <see cref="CompositionOrder.Stages"/> is derived from the
    /// enumeration rather than written out beside it — so there is one place to change and no
    /// second list to fall out of step with it.
    /// </remarks>
    public enum AnalysisStage
    {
        /// <summary>Time gating, on the acquired record (<c>REQ-DSP-050</c>).</summary>
        Gating = 0,

        /// <summary>The analysis window (<c>REQ-DSP-010</c>).</summary>
        Windowing,

        /// <summary>The transform itself (<c>REQ-DSP-001</c>).</summary>
        Transform,

        /// <summary>Averaging across acquisitions (<c>REQ-DSP-030</c>).</summary>
        Averaging,

        /// <summary>Accumulating displays: spectrogram and the rest (<c>REQ-TRC-001a</c>).</summary>
        Accumulation,

        /// <summary>Format conversion for display (<c>REQ-DSP-041</c>).</summary>
        Format,
    }

    /// <summary>Whether a combination of settings may be measured, and why not if not.</summary>
    public readonly struct CompositionVerdict
    {
        internal CompositionVerdict(bool isLegal, string reason)
        {
            IsLegal = isLegal;
            Reason = reason;
        }

        /// <summary>Whether the combination is legal.</summary>
        public bool IsLegal { get; }

        /// <summary>
        /// Why it is not, or an empty string when it is.
        /// </summary>
        /// <remarks>
        /// Never empty for an illegal combination. <c>REQ-TRC-003</c> requires every combination to
        /// be legal or "rejected by a named error; none is silently ignored", and a rejection with
        /// nothing to say is the silent kind wearing a return value.
        /// </remarks>
        public string Reason { get; }

        /// <inheritdoc />
        public override string ToString() => IsLegal ? "legal" : Reason;
    }

    /// <summary>
    /// One combination of the settings whose composition order matters (<c>REQ-TRC-003</c>).
    /// </summary>
    public readonly struct CompositionSelection
    {
        /// <summary>Creates a selection.</summary>
        /// <param name="gated">Whether time gating is applied.</param>
        /// <param name="averaging">The averaging type.</param>
        /// <param name="accumulator">The accumulating display, if any.</param>
        /// <param name="format">The display format.</param>
        public CompositionSelection(
            bool gated,
            AveragingType averaging,
            TraceAccumulator accumulator,
            TraceFormat format)
        {
            Gated = gated;
            Averaging = averaging;
            Accumulator = accumulator;
            Format = format;
        }

        /// <summary>Whether time gating is applied.</summary>
        public bool Gated { get; }

        /// <summary>The averaging type.</summary>
        public AveragingType Averaging { get; }

        /// <summary>The accumulating display, if any.</summary>
        public TraceAccumulator Accumulator { get; }

        /// <summary>The display format.</summary>
        public TraceFormat Format { get; }

        /// <inheritdoc />
        public override string ToString() =>
            (Gated ? "gated" : "ungated") + ", " + Averaging + ", " + Accumulator + ", " + Format;
    }

    /// <summary>
    /// The order the analysis stages compose in, declared once (<c>REQ-TRC-003</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The reason this exists at all.</strong> Gating, windowing, the transform, averaging,
    /// accumulation and format conversion are each specified elsewhere. Without a stated order two
    /// developers will reasonably implement gate-then-average and average-then-gate, and both will
    /// believe they are right — and the two give measurably different answers on a burst.
    /// </para>
    /// <para>
    /// <strong>Windowing is not commutative with gating.</strong> A gate multiplies the record by a
    /// rectangle and the window multiplies it by a taper; multiplication commutes, but the
    /// <em>window's length</em> does not — a window sized to the ungated record and then gated is a
    /// different shape from a window sized to the gate. Gating first, then windowing what survives,
    /// is what makes <c>REQ-DSP-050</c>'s "RBW tracks the gate length" true.
    /// </para>
    /// <para>
    /// <strong>Averaging precedes accumulation.</strong> A spectrogram row is a completed
    /// measurement, so a history of ten-average traces is ten-average traces; accumulating first
    /// and averaging the accumulation would average across history rows, which is a different
    /// measurement and not one anyone asked for.
    /// </para>
    /// <para>
    /// <strong>Format is last, and only last.</strong> <c>REQ-TRC-001</c> requires a format change
    /// to recompute nothing, which is only possible if nothing upstream depends on it.
    /// </para>
    /// </remarks>
    public static class CompositionOrder
    {
        private static readonly ReadOnlyCollection<AnalysisStage> Order =
            new ReadOnlyCollection<AnalysisStage>(Sorted());

        /// <summary>
        /// The stages, in the order they are applied.
        /// </summary>
        /// <remarks>
        /// Derived from <see cref="AnalysisStage"/>'s own values rather than listed again here.
        /// A second list is a second thing to keep in step, and the whole point of this class is
        /// that there is one.
        /// </remarks>
        public static IReadOnlyList<AnalysisStage> Stages => Order;

        /// <summary>Where a stage sits in the order, counting from zero.</summary>
        /// <param name="stage">The stage.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known stage.</exception>
        public static int PositionOf(AnalysisStage stage)
        {
            int position = Order.IndexOf(stage);

            if (position < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stage), stage, "Not a known analysis stage.");
            }

            return position;
        }

        /// <summary>Whether one stage is applied after another.</summary>
        /// <param name="stage">The stage in question.</param>
        /// <param name="other">The stage to compare against.</param>
        /// <exception cref="ArgumentOutOfRangeException">A stage is not known.</exception>
        public static bool IsAfter(AnalysisStage stage, AnalysisStage other) =>
            PositionOf(stage) > PositionOf(other);

        /// <summary>
        /// Whether a combination of settings may be measured.
        /// </summary>
        /// <param name="selection">The combination.</param>
        /// <returns>Legal, or illegal with the reason named.</returns>
        /// <remarks>
        /// <para>
        /// Exhaustive over the cross-product by construction: every selection reaches one of the
        /// rules below or falls through to legal. There is no combination this does not answer for,
        /// which is what <c>REQ-TRC-003</c>'s "none is silently ignored" asks.
        /// </para>
        /// <para>
        /// Both rules follow from the order rather than from taste. Averaging is applied before
        /// format, so a format needing phase cannot recover what a power average already discarded;
        /// accumulation is applied before format, so a spectrogram has to reduce each row to one
        /// number before any format is chosen, and the complex pair is not one number.
        /// </para>
        /// </remarks>
        public static CompositionVerdict Validate(CompositionSelection selection)
        {
            if (!TraceValidity.IsValid(selection.Format, selection.Averaging))
            {
                return new CompositionVerdict(
                    false, TraceValidity.Explain(selection.Format, selection.Averaging));
            }

            if (selection.Accumulator == TraceAccumulator.Spectrogram &&
                selection.Format == TraceFormat.IQ)
            {
                return new CompositionVerdict(
                    false,
                    "A spectrogram colours one number per bin per row, and " + TraceFormat.IQ +
                    " is a pair. Choose a scalar format, or turn the spectrogram off.");
            }

            return new CompositionVerdict(true, string.Empty);
        }

        /// <summary>
        /// Every combination of the settings whose order matters, with its verdict.
        /// </summary>
        /// <returns>
        /// The full cross-product of gating, averaging, accumulation and format.
        /// </returns>
        /// <remarks>
        /// Windowing is a stage in the order but contributes no legality of its own — every window
        /// is legal with everything — so it is not a dimension of this product. Enumerating it
        /// would multiply the list by the window count and say nothing.
        /// </remarks>
        public static IReadOnlyList<KeyValuePair<CompositionSelection, CompositionVerdict>>
            AllCombinations()
        {
            var all = new List<KeyValuePair<CompositionSelection, CompositionVerdict>>();

            foreach (bool gated in new[] { false, true })
            {
                foreach (AveragingType averaging in Enum.GetValues(typeof(AveragingType)))
                {
                    foreach (TraceAccumulator accumulator in
                        Enum.GetValues(typeof(TraceAccumulator)))
                    {
                        foreach (TraceFormat format in Enum.GetValues(typeof(TraceFormat)))
                        {
                            var selection = new CompositionSelection(
                                gated, averaging, accumulator, format);

                            all.Add(
                                new KeyValuePair<CompositionSelection, CompositionVerdict>(
                                    selection, Validate(selection)));
                        }
                    }
                }
            }

            return new ReadOnlyCollection<
                KeyValuePair<CompositionSelection, CompositionVerdict>>(all);
        }

        private static AnalysisStage[] Sorted()
        {
            var stages = (AnalysisStage[])Enum.GetValues(typeof(AnalysisStage));

            Array.Sort(stages, (a, b) => ((int)a).CompareTo((int)b));

            return stages;
        }
    }
}
