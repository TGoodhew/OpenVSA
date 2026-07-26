using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using OpenVSA.Core;
using OpenVSA.Dsp.Windowing;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// Runs one acquisition through the analysis stages, in the order
    /// <see cref="CompositionOrder"/> declares (<c>REQ-TRC-003</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The loop walks the declaration.</strong> <see cref="Run"/> iterates
    /// <see cref="CompositionOrder.Stages"/> and dispatches each one; it does not apply the stages
    /// in an order written out here. Reordering the declaration therefore reorders the
    /// implementation, which is what <c>REQ-TRC-003</c> means by "the implementation is driven by
    /// that declaration rather than restating it". A second hard-coded sequence — even one that
    /// currently agrees — is exactly the thing this requirement exists to prevent.
    /// </para>
    /// <para>
    /// <strong>Windowing and the transform are one call, and the pipeline says so.</strong>
    /// <see cref="SpectrumComputer"/> windows and transforms in a single pass over the samples,
    /// because at 2²⁰ points a second sweep is 16 MB of traffic for nothing. The two stages are
    /// still declared separately and still visited separately here: the windowing step captures the
    /// window to be applied, the transform step is where it happens. Collapsing them in the
    /// declaration to match the implementation would hide a real ordering constraint behind an
    /// optimisation.
    /// </para>
    /// <para>
    /// <strong>The order of invocation is recorded.</strong> <see cref="LastRunStages"/> is what
    /// makes the criterion testable at all: a test can assert that what ran, ran in the declared
    /// order, rather than reading the source and agreeing with it.
    /// </para>
    /// </remarks>
    public sealed class AnalysisPipeline
    {
        private readonly List<AnalysisStage> _ran = new List<AnalysisStage>();

        /// <summary>Creates a pipeline over a computer.</summary>
        /// <param name="computer">The spectrum computer; its window is the analysis window.</param>
        /// <exception cref="ArgumentNullException"><paramref name="computer"/> is null.</exception>
        public AnalysisPipeline(SpectrumComputer computer)
        {
            if (computer == null)
            {
                throw new ArgumentNullException(nameof(computer));
            }

            Computer = computer;
        }

        /// <summary>The spectrum computer, which windows and transforms.</summary>
        public SpectrumComputer Computer { get; }

        /// <summary>The time gate, or <c>null</c> for no gating (<c>REQ-DSP-050</c>).</summary>
        public TimeGate Gate { get; set; }

        /// <summary>The averager, or <c>null</c> for no averaging (<c>REQ-DSP-030</c>).</summary>
        public TraceAverager Averager { get; set; }

        /// <summary>The accumulating display, or <c>null</c> for none (<c>REQ-TRC-001a</c>).</summary>
        public AccumulatingTrace Accumulator { get; set; }

        /// <summary>The display format applied at the end (<c>REQ-DSP-041</c>).</summary>
        public TraceFormat Format { get; set; } = TraceFormat.LogMagnitude;

        /// <summary>The stages that ran on the last <see cref="Run"/>, in the order they ran.</summary>
        public IReadOnlyList<AnalysisStage> LastRunStages =>
            new ReadOnlyCollection<AnalysisStage>(_ran);

        /// <summary>
        /// The settings this pipeline is configured with, as a combination to be validated.
        /// </summary>
        public CompositionSelection Selection =>
            new CompositionSelection(
                Gate != null,
                Averager == null ? AveragingType.Off : Averager.Type,
                Accumulator == null ? TraceAccumulator.None : Accumulator.Accumulator,
                Format);

        /// <summary>
        /// Runs one acquisition through every stage.
        /// </summary>
        /// <param name="block">The acquired samples; not retained.</param>
        /// <returns>
        /// The formatted trace: <c>PointCount × TraceFormatter.ValuesPerPoint(Format)</c> values.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="block"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// The configured combination is illegal; the message is the named reason.
        /// </exception>
        public float[] Run(IqBlock block)
        {
            if (block == null)
            {
                throw new ArgumentNullException(nameof(block));
            }

            CompositionVerdict verdict = CompositionOrder.Validate(Selection);

            if (!verdict.IsLegal)
            {
                throw new InvalidOperationException(verdict.Reason);
            }

            _ran.Clear();

            IqBlock working = block;
            IqBlock gated = null;
            SpectrumFrame frame = null;
            Window window = null;
            float[] view = null;

            try
            {
                foreach (AnalysisStage stage in CompositionOrder.Stages)
                {
                    switch (stage)
                    {
                        case AnalysisStage.Gating:
                            if (Gate != null)
                            {
                                gated = Gate.Apply(working);
                                working = gated;
                                _ran.Add(stage);
                            }

                            break;

                        case AnalysisStage.Windowing:
                            // The window that the transform will apply, resolved here so the stage
                            // is visited where the order says it is. Gating first is what makes the
                            // length - and so REQ-DSP-050's RBW - follow the gate.
                            window = Window.Get(
                                Computer.WindowType,
                                SpectrumComputer.TransformLengthFor(working.SampleCount));
                            _ran.Add(stage);
                            break;

                        case AnalysisStage.Transform:
                            frame = Computer.Compute(working);
                            _ran.Add(stage);
                            break;

                        case AnalysisStage.Averaging:
                            if (Averager != null)
                            {
                                frame = Averager.Accumulate(frame);
                                _ran.Add(stage);
                            }

                            break;

                        case AnalysisStage.Accumulation:
                            if (Accumulator != null)
                            {
                                Accumulator.Add(frame);
                                _ran.Add(stage);
                            }

                            break;

                        case AnalysisStage.Format:
                            view = new float[
                                frame.PointCount * TraceFormatter.ValuesPerPoint(Format)];
                            frame.Format(Format, new Span<float>(view));
                            _ran.Add(stage);
                            break;

                        default:
                            throw new InvalidOperationException(
                                "The composition order names a stage this pipeline does not run: " +
                                stage + ". A stage added to the declaration must be added here, " +
                                "or it is declared and never applied.");
                    }
                }
            }
            finally
            {
                if (gated != null)
                {
                    gated.Dispose();
                }
            }

            LastFrame = frame;
            LastWindow = window;

            return view;
        }

        /// <summary>The frame the last run produced, before formatting.</summary>
        public SpectrumFrame LastFrame { get; private set; }

        /// <summary>The window the last run resolved, whose length follows the gate.</summary>
        public Window LastWindow { get; private set; }
    }
}
