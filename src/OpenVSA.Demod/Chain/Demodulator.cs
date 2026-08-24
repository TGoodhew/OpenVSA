using System;
using System.Collections.Generic;
using System.Globalization;
using OpenVSA.Demod.Chain.Steps;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Chain
{
    /// <summary>
    /// Runs the demodulation chain, in the order <see cref="ProcessingOrder"/> declares
    /// (<c>REQ-DEM-001</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The order is walked, not written out.</strong> This class contains no sequence of
    /// fourteen calls. It iterates <see cref="ProcessingOrder.Steps"/> and asks the registry for
    /// the handler of each — so moving a value in <see cref="DemodStep"/> moves the execution, and
    /// there is no second place where the order is repeated and could disagree. That is what
    /// <c>REQ-DEM-001</c> means by "the implementation is driven by that declaration".
    /// </para>
    /// <para>
    /// <strong>Every declared step must have a handler.</strong> Construction fails if one does
    /// not, rather than the chain quietly skipping it at run time. Adding a step to the enumeration
    /// therefore breaks the build until something implements it, which is the failure mode worth
    /// having: the alternative is a demodulator that silently omits a stage of the specification.
    /// </para>
    /// <para>
    /// <strong>The re-entry completes its pass first.</strong> When the equaliser updates its
    /// coefficients it asks to re-enter at step 8, and this chain finishes the pass — steps 12, 13
    /// and 14 included — before doing so. The alternative reading, jumping back from step 11 the
    /// moment the coefficients move, would leave the first pass with no metrics of its own, and the
    /// requirement's own criterion is a comparison between passes: "a signal whose EVM improves on
    /// the second pass". A pass that produced no EVM could not be the first half of that
    /// comparison. The cost is steps 12 to 14 running once more than strictly needed, which is a
    /// few thousand operations against step 8's iteration over the whole block.
    /// </para>
    /// </remarks>
    public sealed class Demodulator
    {
        private readonly Dictionary<DemodStep, IChainStep> _steps;

        /// <summary>Creates a demodulator with the chain's own steps.</summary>
        public Demodulator()
            : this(DefaultSteps())
        {
        }

        /// <summary>Creates a demodulator over a given set of steps.</summary>
        /// <param name="steps">A handler for every step the order declares.</param>
        /// <exception cref="ArgumentNullException"><paramref name="steps"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// A declared step has no handler, or a handler was registered under a step other than the
        /// one it says it is.
        /// </exception>
        /// <remarks>
        /// Internal because a step is defined in terms of <see cref="DemodContext"/>. The tests use
        /// it to substitute a step that misbehaves on purpose, which is the only way to show that
        /// the order is enforced rather than merely followed.
        /// </remarks>
        internal Demodulator(IDictionary<DemodStep, IChainStep> steps)
        {
            if (steps == null)
            {
                throw new ArgumentNullException(nameof(steps));
            }

            _steps = new Dictionary<DemodStep, IChainStep>();

            foreach (KeyValuePair<DemodStep, IChainStep> registration in steps)
            {
                if (registration.Value == null)
                {
                    throw new InvalidOperationException(
                        "Step " + ProcessingOrder.NumberOf(registration.Key) + " (" +
                        registration.Key + ") was registered as null.");
                }

                if (registration.Value.Step != registration.Key)
                {
                    throw new InvalidOperationException(
                        "A handler that says it is step " +
                        ProcessingOrder.NumberOf(registration.Value.Step) + " (" +
                        registration.Value.Step + ") was registered as step " +
                        ProcessingOrder.NumberOf(registration.Key) + " (" + registration.Key +
                        "). REQ-DEM-001's order is only worth declaring if the handlers sit where " +
                        "it puts them.");
                }

                _steps[registration.Key] = registration.Value;
            }

            var missing = new List<string>();

            foreach (DemodStep step in ProcessingOrder.Steps)
            {
                if (!_steps.ContainsKey(step))
                {
                    missing.Add(
                        ProcessingOrder.NumberOf(step).ToString(CultureInfo.InvariantCulture) +
                        " (" + step + ")");
                }
            }

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    "The chain has no handler for step " + string.Join(", step ", missing.ToArray()) +
                    ". Every step REQ-DEM-001 declares is executed, so every one of them needs " +
                    "something to execute.");
            }
        }

        /// <summary>
        /// Demodulates a record.
        /// </summary>
        /// <param name="mainTime">
        /// The acquired record, interleaved real and imaginary as <c>REQ-DAT-003</c> carries bulk
        /// samples.
        /// </param>
        /// <param name="sampleRateHz">The rate <paramref name="mainTime"/> was sampled at.</param>
        /// <param name="settings">What to demodulate it as.</param>
        /// <returns>The result, with the account of how it was reached.</returns>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        /// <exception cref="ArgumentException">
        /// A setting is outside its range, or the record is too short for the settings.
        /// </exception>
        /// <exception cref="ChainOrderException">A step executed out of the declared order.</exception>
        public DemodResult Run(float[] mainTime, double sampleRateHz, DemodSettings settings)
        {
            if (mainTime == null)
            {
                throw new ArgumentNullException(nameof(mainTime));
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (sampleRateHz <= 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sampleRateHz), sampleRateHz, "The sample rate is positive.");
            }

            settings.Validate();

            return Execute(mainTime, sampleRateHz, settings);
        }

        /// <summary>The equaliser's taps as points, or null when it did not run.</summary>
        private static List<ConstellationPoint> Points(Iq[] taps)
        {
            if (taps == null)
            {
                return null;
            }

            var points = new List<ConstellationPoint>(taps.Length);

            foreach (Iq tap in taps)
            {
                points.Add(new ConstellationPoint(tap.I, tap.Q));
            }

            return points;
        }

        private static Dictionary<DemodStep, IChainStep> DefaultSteps()
        {
            var steps = new IChainStep[]
            {
                new SearchWindowStep(),
                new BurstSearchStep(),
                new CoarseCarrierStep(),
                new ResampleStep(),
                new MeasurementFilterStep(),
                new SyncSearchStep(),
                new ResultWindowStep(),
                new JointRefinementStep(),
                new SymbolDecisionStep(),
                new ReferenceRegenerationStep(),
                new EqualiserStep(),
                new ImpairmentStep(),
                new ErrorMetricStep(),
                new ResultTraceStep(),
            };

            var registry = new Dictionary<DemodStep, IChainStep>();

            foreach (IChainStep step in steps)
            {
                registry[step.Step] = step;
            }

            return registry;
        }

        private DemodResult Execute(float[] record, double sampleRateHz, DemodSettings settings)
        {
            var journal = new ChainJournal();
            var context = new DemodContext(record, sampleRateHz, settings, journal);
            var passes = new List<PassResult>();

            int pass = 1;
            int start = 0;

            while (true)
            {
                context.Pass = pass;
                context.EqualiserUpdated = false;

                bool equaliserRan = false;
                bool reEnter = false;

                for (int index = start; index < ProcessingOrder.Steps.Count; index++)
                {
                    DemodStep step = ProcessingOrder.Steps[index];

                    bool enabled = !ProcessingOrder.IsOptional(step) || settings.IsEnabled(step);

                    journal.Record(pass, step, enabled);

                    if (!enabled)
                    {
                        continue;
                    }

                    StepOutcome outcome = _steps[step].Run(context);

                    if (step == DemodStep.Equaliser)
                    {
                        equaliserRan = true;
                    }

                    if (outcome != StepOutcome.ReEnter)
                    {
                        continue;
                    }

                    if (step != DemodStep.Equaliser)
                    {
                        throw new ChainOrderException(
                            "Step " + ProcessingOrder.NumberOf(step) + " (" + step +
                            ") asked the chain to re-enter. REQ-DEM-001 gives that loop to the " +
                            "equaliser alone.");
                    }

                    reEnter = true;
                }

                passes.Add(
                    new PassResult(pass, context.Convergence, context.EvmPercent, equaliserRan));

                if (!reEnter)
                {
                    break;
                }

                if (pass >= settings.MaxPasses)
                {
                    context.Note(
                        "The equaliser was still updating its coefficients when the chain reached " +
                        "its bound of " + settings.MaxPasses.ToString(CultureInfo.InvariantCulture) +
                        " pass(es). The result is the last pass's, and it is not the one the " +
                        "equaliser was heading towards.");

                    break;
                }

                pass++;
                start = ProcessingOrder.PositionOf(ProcessingOrder.ReEntryPoint);
            }

            // REQ-DEM-072: built from the same settings, in the same pass, as the metrics. That is
            // what makes "the two can never disagree" structural rather than a discipline: one
            // result carries both, so anything handed the metrics has already been handed the
            // context that qualifies them.
            var provenance = new MeasurementProvenance(
                context.Summary == null ? null : context.Summary.Reference,
                settings.MeasurementPulse.ToString(),
                settings.ReferencePulse.ToString(),
                settings.FilterSymbolSpan,
                settings.EqualiserEnabled,
                settings.MirrorSpectrum,
                settings.BurstSearchEnabled,
                settings.SyncSearchEnabled);

            // REQ-DEM-036: the judgement is made once, at the end, on everything the chain has --
            // and the explanation goes into the notices as well as into the result, because the
            // shell already says the notices out loud and a diagnosis nobody is shown is not one.
            LockReport judgement = LockDiagnosis.For(context);

            if (!judgement.Locked)
            {
                context.Note(judgement.Explanation);
            }

            return new DemodResult(
                context.Trace,
                context.Summary,
                context.Symbols,
                context.DataSymbols,
                context.Bits,
                context.CoarseFrequencyHz + context.ResidualFrequencyHz,
                context.Impairments,
                passes,
                journal,
                new List<string>(context.Notices),
                context.ReferenceWaveform,
                Points(context.EqualiserCoefficients),
                judgement,
                provenance);
        }
    }
}
