using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Demod.Chain;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// <c>REQ-DEM-001</c>: the order is declared once, it is the order the specification gives, and
    /// the declaration answers for optionality and for the equaliser's re-entry.
    /// </summary>
    public class ProcessingOrderTests
    {
        private readonly ITestOutputHelper _output;

        public ProcessingOrderTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TheOrderIsTheOneTheRequirementNames()
        {
            Assert.Equal(
                new[]
                {
                    DemodStep.SearchWindow,
                    DemodStep.BurstSearch,
                    DemodStep.CoarseCarrier,
                    DemodStep.Resample,
                    DemodStep.MeasurementFilter,
                    DemodStep.SyncSearch,
                    DemodStep.ResultWindow,
                    DemodStep.JointRefinement,
                    DemodStep.SymbolDecisions,
                    DemodStep.ReferenceRegeneration,
                    DemodStep.Equaliser,
                    DemodStep.ImpairmentEstimation,
                    DemodStep.ErrorMetrics,
                    DemodStep.ResultTraces,
                },
                ProcessingOrder.Steps.ToArray());
        }

        [Fact]
        public void TheOrderIsDerivedFromTheEnumerationRatherThanListedBesideIt()
        {
            // The property REQ-DEM-001 asks for is that the order is declared ONCE. A second list
            // would pass every test above and drift from the enumeration the first time a step
            // moved, so what is asserted here is that there is no second list: every value appears,
            // exactly once, in the order its number gives.
            var values = (DemodStep[])Enum.GetValues(typeof(DemodStep));

            Assert.Equal(values.Length, ProcessingOrder.Steps.Count);
            Assert.Equal(values.OrderBy(step => (int)step).ToArray(), ProcessingOrder.Steps.ToArray());

            foreach (DemodStep step in values)
            {
                Assert.Equal((int)step, ProcessingOrder.NumberOf(step));
                Assert.Equal((int)step - 1, ProcessingOrder.PositionOf(step));
            }
        }

        [Fact]
        public void TheThreeOptionalStepsAreTheOnesTheRequirementMarks()
        {
            Assert.Equal(
                new[] { DemodStep.BurstSearch, DemodStep.SyncSearch, DemodStep.Equaliser },
                ProcessingOrder.Steps.Where(ProcessingOrder.IsOptional).ToArray());

            Assert.Equal(
                new[] { 2, 6, 11 },
                ProcessingOrder.Steps
                    .Where(ProcessingOrder.IsOptional)
                    .Select(ProcessingOrder.NumberOf)
                    .ToArray());
        }

        [Fact]
        public void TheReEntryPointIsStepEight()
        {
            Assert.Equal(DemodStep.JointRefinement, ProcessingOrder.ReEntryPoint);
            Assert.Equal(8, ProcessingOrder.NumberOf(ProcessingOrder.ReEntryPoint));

            // And it is before the equaliser, or the loop would not be a loop.
            Assert.True(
                ProcessingOrder.IsAfter(DemodStep.Equaliser, ProcessingOrder.ReEntryPoint));
        }

        [Fact]
        public void AnnotationsFollowTheDeclarationRatherThanBeingWrittenOut()
        {
            Assert.EndsWith("(optional)", ProcessingOrder.Describe(DemodStep.BurstSearch), StringComparison.Ordinal);
            Assert.EndsWith("(optional)", ProcessingOrder.Describe(DemodStep.SyncSearch), StringComparison.Ordinal);

            Assert.EndsWith(
                "(optional; re-enters at 8 on update)",
                ProcessingOrder.Describe(DemodStep.Equaliser),
                StringComparison.Ordinal);

            Assert.Equal(
                ProcessingOrder.TitleOf(DemodStep.Resample),
                ProcessingOrder.Describe(DemodStep.Resample));
        }

        [Fact]
        public void RenderingGivesOneNumberedLinePerStep()
        {
            IReadOnlyList<string> lines = ProcessingOrder.Render();

            Assert.Equal(ProcessingOrder.Steps.Count, lines.Count);

            for (int index = 0; index < lines.Count; index++)
            {
                Assert.StartsWith(
                    (index + 1) + ". ", lines[index], StringComparison.Ordinal);
            }

            foreach (string line in lines)
            {
                _output.WriteLine(line);
            }
        }

        [Fact]
        public void AStepThatIsNotOneIsRefusedRatherThanGivenAPosition()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ProcessingOrder.PositionOf((DemodStep)99));
            Assert.Throws<ArgumentOutOfRangeException>(() => ProcessingOrder.IsOptional((DemodStep)0));
            Assert.Throws<ArgumentOutOfRangeException>(() => ProcessingOrder.TitleOf((DemodStep)15));
        }
    }
}
