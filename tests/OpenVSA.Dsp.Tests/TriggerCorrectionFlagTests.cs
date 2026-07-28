using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Zoom;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-DAT-002</c>: the trigger-correction flag survives every transformation the analysis
    /// layers apply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rationale is a real trap the reference product documents: trigger corrections are
    /// <strong>not</strong> applied to its exported data except in one format, and nothing says so
    /// at the point of use. A measurement carried through three transformations and then exported
    /// with the flag defaulted is wrong in a way that leaves no trace — the samples are fine, and
    /// the statement about what was done to them is lost.
    /// </para>
    /// <para>
    /// <strong>Both values, through every transformation.</strong> A propagation bug that hard-wired
    /// <c>false</c> would pass a test that only ever fed it <c>false</c>, and defaulting is exactly
    /// what a bool does when somebody forgets to pass it.
    /// </para>
    /// </remarks>
    public class TriggerCorrectionFlagTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the surviving flags are written.</param>
        public TriggerCorrectionFlagTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void FrameExtractionPreservesTheFlag(bool applied)
        {
            IqBlock block = Block(applied, 8192);

            IqBlock frame = FrameExtraction.Extract(block, 4096, 0.0).First();

            _output.WriteLine("extraction: " + applied + " -> " + frame.TriggerCorrectionsApplied);
            Assert.Equal(applied, frame.TriggerCorrectionsApplied);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TimeGatingPreservesTheFlag(bool applied)
        {
            IqBlock block = Block(applied, 8192);

            IqBlock gated = new TimeGate(0.0005, 0.002).Apply(block);

            _output.WriteLine("gating: " + applied + " -> " + gated.TriggerCorrectionsApplied);
            Assert.Equal(applied, gated.TriggerCorrectionsApplied);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void DownconversionPreservesTheFlag(bool applied)
        {
            IqBlock block = Block(applied, 8192);

            IqBlock zoomed = DigitalDownconverter.ForDecimation(block.SampleRateHz, 0.0, 4).Downconvert(block);

            _output.WriteLine("downconversion: " + applied + " -> " + zoomed.TriggerCorrectionsApplied);
            Assert.Equal(applied, zoomed.TriggerCorrectionsApplied);
        }

        [Fact]
        public void TheFlagSurvivesAChainOfTransformations()
        {
            // One transformation preserving it is not the claim. A measurement goes through
            // several, and a single defaulting step anywhere in the chain loses the statement for
            // everything downstream — which is precisely how this gets lost in practice.
            IqBlock block = Block(applied: true, samples: 16384);

            IqBlock result = FrameExtraction.Extract(block, 8192, 0.0).First();
            result = new TimeGate(0.0002, 0.001).Apply(result);
            result = DigitalDownconverter.ForDecimation(result.SampleRateHz, 0.0, 2).Downconvert(result);

            Assert.True(
                result.TriggerCorrectionsApplied,
                "The flag was lost somewhere in extraction, gating and downconversion.");

            // And the same chain carries false through as false, so nothing along it hard-wires
            // the answer.
            IqBlock uncorrected = Block(applied: false, samples: 16384);

            IqBlock second = FrameExtraction.Extract(uncorrected, 8192, 0.0).First();
            second = new TimeGate(0.0002, 0.001).Apply(second);
            second = DigitalDownconverter.ForDecimation(second.SampleRateHz, 0.0, 2).Downconvert(second);

            Assert.False(second.TriggerCorrectionsApplied);
        }

        [Fact]
        public void EveryTransformationThatBuildsABlockIsCovered()
        {
            // The list above is hand-written, so it goes stale silently the moment somebody adds a
            // fourth transformation. This fails when that happens: any public method in the DSP
            // assembly that takes an IqBlock and returns one is a transformation, and each must be
            // named here.
            string[] covered = { "Extract", "Apply", "Downconvert" };

            var found = new List<string>();

            foreach (Type type in Assembly.Load("OpenVSA.Dsp").GetExportedTypes())
            {
                foreach (MethodInfo method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.DeclaredOnly))
                {
                    bool returnsBlocks =
                        method.ReturnType == typeof(IqBlock) ||
                        (method.ReturnType.IsGenericType &&
                         method.ReturnType.GetGenericArguments().Any(a => a == typeof(IqBlock)));

                    if (returnsBlocks &&
                        method.GetParameters().Any(p => p.ParameterType == typeof(IqBlock)))
                    {
                        found.Add(type.Name + "." + method.Name);
                    }
                }
            }

            _output.WriteLine("block-to-block transformations: " + string.Join(", ", found));

            string[] uncovered = found
                .Where(f => !covered.Contains(f.Split('.')[1]))
                .ToArray();

            Assert.False(
                uncovered.Any(),
                "A transformation takes an IqBlock and returns one but has no flag-propagation " +
                "test: " + string.Join(", ", uncovered) +
                ". Add it above, or REQ-DAT-002 is unenforced for it.");
        }

        private static IqBlock Block(bool applied, int samples)
        {
            var metadata = new IqBlockMetadata(
                samples, 2.0e6, 1.0e9, false, 1.0, 0.0, 1L,
                new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc), 0.0, applied,
                new FrontEndId("test"), null);

            IqBlock block = IqBlock.Rent(metadata);
            Span<float> data = block.GetSamples();

            for (int n = 0; n < samples; n++)
            {
                data[n * 2] = (float)Math.Cos(0.1 * n);
                data[n * 2 + 1] = (float)Math.Sin(0.1 * n);
            }

            return block;
        }
    }
}
