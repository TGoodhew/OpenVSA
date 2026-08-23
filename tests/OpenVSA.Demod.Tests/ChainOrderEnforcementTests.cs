using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Chain.Steps;
using OpenVSA.Demod.Tests.Signals;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// <c>REQ-DEM-001</c>: "a test fails if any step executes out of declared order". These are
    /// those tests, and each one provokes a different way of getting the order wrong.
    /// </summary>
    /// <remarks>
    /// The chain executes by walking the declaration, so the obvious out-of-order execution cannot
    /// happen by accident — which is the point of building it that way. What can still happen is a
    /// step reaching sideways into another, a handler wired into the wrong slot, a step going
    /// missing, or something other than the equaliser helping itself to the loop. Every one of
    /// those is provoked here rather than reasoned about.
    /// </remarks>
    public class ChainOrderEnforcementTests
    {
        private readonly ITestOutputHelper _output;

        public ChainOrderEnforcementTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TheJournalRefusesAStepThatComesBeforeOneAlreadyRecorded()
        {
            var journal = new ChainJournal();

            journal.Record(1, DemodStep.SearchWindow, true);
            journal.Record(1, DemodStep.CoarseCarrier, true);
            journal.Record(1, DemodStep.MeasurementFilter, true);

            ChainOrderException failure = Assert.Throws<ChainOrderException>(
                () => journal.Record(1, DemodStep.Resample, true));

            _output.WriteLine(failure.Message);

            Assert.Contains("step 4", failure.Message, StringComparison.Ordinal);
            Assert.Contains("step 5", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TheJournalRefusesAStepRunTwiceInOnePass()
        {
            var journal = new ChainJournal();

            journal.Record(1, DemodStep.SearchWindow, true);

            Assert.Throws<ChainOrderException>(
                () => journal.Record(1, DemodStep.SearchWindow, true));
        }

        [Fact]
        public void TheJournalRefusesAChainThatDoesNotStartAtTheStart()
        {
            var journal = new ChainJournal();

            Assert.Throws<ChainOrderException>(
                () => journal.Record(1, DemodStep.CoarseCarrier, true));
        }

        [Fact]
        public void ALaterPassMayOnlyBeginAtTheDeclaredReEntryPoint()
        {
            var journal = Complete();

            // The one backward movement the specification allows.
            journal.Record(2, ProcessingOrder.ReEntryPoint, true);

            var second = Complete();

            ChainOrderException failure = Assert.Throws<ChainOrderException>(
                () => second.Record(2, DemodStep.SearchWindow, true));

            _output.WriteLine(failure.Message);

            Assert.Contains("re-enter", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void PassesAreConsecutive()
        {
            ChainJournal journal = Complete();

            Assert.Throws<ChainOrderException>(
                () => journal.Record(3, ProcessingOrder.ReEntryPoint, true));
        }

        [Fact]
        public void APassMayNotReEnterBeforeTheEqualiserHasRun()
        {
            var journal = new ChainJournal();

            foreach (DemodStep step in ProcessingOrder.Steps.TakeWhile(
                step => !ProcessingOrder.IsAfter(step, DemodStep.SymbolDecisions)))
            {
                journal.Record(1, step, true);
            }

            // Nothing in a pass that stopped at step 9 could have asked to go round again.
            ChainOrderException failure = Assert.Throws<ChainOrderException>(
                () => journal.Record(2, ProcessingOrder.ReEntryPoint, true));

            _output.WriteLine(failure.Message);
        }

        [Fact]
        public void AStepThatReachesBackIntoAnEarlierOneFailsTheChain()
        {
            // The realistic drift: a step that decides it needs to redo something, and does it
            // where it stands rather than through the chain. It announces itself through the
            // journal like every other step, and the journal is what refuses it.
            var rogue = new RogueStep(
                DemodStep.ImpairmentEstimation, DemodStep.JointRefinement);

            ChainOrderException failure = Assert.Throws<ChainOrderException>(
                () => Run(Substituting(rogue)));

            _output.WriteLine(failure.Message);

            Assert.Contains("step 8", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AHandlerRegisteredUnderTheWrongStepIsRefused()
        {
            Dictionary<DemodStep, IChainStep> steps = Default();

            steps[DemodStep.ErrorMetrics] = new ResultTraceStep();

            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
                () => new Demodulator(steps));

            _output.WriteLine(failure.Message);

            Assert.Contains("registered as step 13", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ADeclaredStepWithNoHandlerIsRefused()
        {
            Dictionary<DemodStep, IChainStep> steps = Default();

            steps.Remove(DemodStep.SyncSearch);

            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
                () => new Demodulator(steps));

            _output.WriteLine(failure.Message);

            Assert.Contains("6 (SyncSearch)", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void OnlyTheEqualiserMayAskToReEnter()
        {
            var greedy = new ReEnteringStep(DemodStep.SymbolDecisions);

            ChainOrderException failure = Assert.Throws<ChainOrderException>(
                () => Run(Substituting(greedy)));

            _output.WriteLine(failure.Message);

            Assert.Contains("equaliser alone", failure.Message, StringComparison.Ordinal);
        }

        private static ChainJournal Complete()
        {
            var journal = new ChainJournal();

            foreach (DemodStep step in ProcessingOrder.Steps)
            {
                journal.Record(1, step, true);
            }

            return journal;
        }

        private static Dictionary<DemodStep, IChainStep> Default()
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

            return steps.ToDictionary(step => step.Step, step => step);
        }

        private static Dictionary<DemodStep, IChainStep> Substituting(IChainStep replacement)
        {
            Dictionary<DemodStep, IChainStep> steps = Default();

            steps[replacement.Step] = replacement;

            return steps;
        }

        private static void Run(Dictionary<DemodStep, IChainStep> steps)
        {
            var source = new QpskSource(3) { Amplitude = 0.5 };

            var settings = new DemodSettings
            {
                SymbolRateHz = source.SymbolRateHz,
                ResultLengthSymbols = 128,
            };

            new Demodulator(steps).Run(source.Generate(300), source.SampleRateHz, settings);
        }

        /// <summary>A step that runs an earlier step's work while pretending to be itself.</summary>
        private sealed class RogueStep : IChainStep
        {
            private readonly DemodStep _reachesBackTo;

            internal RogueStep(DemodStep step, DemodStep reachesBackTo)
            {
                Step = step;
                _reachesBackTo = reachesBackTo;
            }

            public DemodStep Step { get; }

            public StepOutcome Run(DemodContext context)
            {
                context.Journal.Record(context.Pass, _reachesBackTo, true);

                return StepOutcome.Continue;
            }
        }

        /// <summary>A step that helps itself to the equaliser's loop.</summary>
        private sealed class ReEnteringStep : IChainStep
        {
            internal ReEnteringStep(DemodStep step)
            {
                Step = step;
            }

            public DemodStep Step { get; }

            public StepOutcome Run(DemodContext context) => StepOutcome.ReEnter;
        }
    }
}
