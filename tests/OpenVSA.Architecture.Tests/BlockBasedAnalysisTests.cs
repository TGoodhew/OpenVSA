using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Architecture.Tests
{
    /// <summary>
    /// <c>REQ-DSP-001</c>: no analysis entry point takes an incremental or push-style form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The requirement's rationale is that whole-block, non-causal estimation is what makes the
    /// reference product's estimation quality achievable — a deliberate architectural choice, not a
    /// compromise. The way it erodes is one convenient push at a time, each of which looks like a
    /// small efficiency and none of which is obviously wrong on its own.
    /// </para>
    /// <para>
    /// <strong>The rule is about entry points, not about all state.</strong> A running average over
    /// completed frames is a measurement the product offers, and forbidding it would be forbidding
    /// averaging. What is forbidden is an analysis whose <em>input</em> arrives a sample at a time,
    /// because that is the form which cannot see the whole block and so cannot estimate over it.
    /// </para>
    /// </remarks>
    public class BlockBasedAnalysisTests
    {
        /// <summary>Names that mean "hand me one sample and remember where you were".</summary>
        /// <remarks>
        /// "Advance" is deliberately NOT here, though it was in the first version. FrameExtraction
        /// .Advance is a pure function returning the advance between successive frames in samples —
        /// a noun, not a verb, and exactly the kind of block-shaped arithmetic this requirement
        /// wants. A name list is a blunt instrument and its false positives have to be looked at
        /// rather than suppressed, or the check gets disabled the first time it is inconvenient.
        /// </remarks>
        private static readonly string[] PushStyleNames =
        {
            "Push", "Feed", "PushSample", "NextSample", "OnSample", "Accept", "Ingest",
        };

        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the examined surface is written.</param>
        public BlockBasedAnalysisTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void NoAnalysisEntryPointIsPushStyle()
        {
            Assembly dsp = Assembly.Load("OpenVSA.Dsp");

            var offenders = new List<string>();
            int examined = 0;

            foreach (Type type in dsp.GetExportedTypes())
            {
                foreach (MethodInfo method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                    BindingFlags.DeclaredOnly))
                {
                    examined++;

                    if (PushStyleNames.Contains(method.Name, StringComparer.Ordinal))
                    {
                        offenders.Add(type.Name + "." + method.Name + " is a push-style entry point");
                        continue;
                    }

                    if (method.Name == "Process" && IsPerSample(method))
                    {
                        offenders.Add(
                            type.Name + ".Process(" +
                            string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name)) +
                            ") takes a single sample rather than a block");
                    }
                }
            }

            _output.WriteLine(examined + " public DSP methods examined");

            Assert.True(examined > 100, "Only " + examined + " methods were examined.");

            Assert.False(
                offenders.Any(),
                "REQ-DSP-001: analysis operates on finite blocks with full random access. A " +
                "per-sample entry point cannot see the whole block, and whole-block estimation is " +
                "what the DSP design rests on." + Environment.NewLine +
                string.Join(Environment.NewLine, offenders));
        }

        [Fact]
        public void TheCheckWouldNoticeAPushStyleEntryPoint()
        {
            // A scan that cannot fail is not a scan. These are the shapes it exists to catch,
            // applied to the same predicates the scan uses.
            Assert.Contains("Push", PushStyleNames);
            Assert.Contains("Feed", PushStyleNames);

            MethodInfo perSample = typeof(Sample).GetMethod(nameof(Sample.Process));
            MethodInfo perBlock = typeof(Sample).GetMethod(nameof(Sample.ProcessBlock));

            Assert.True(IsPerSample(perSample), "A double parameter is a single sample.");
            Assert.False(IsPerSample(perBlock), "A float[] parameter is a block.");
        }

        /// <summary>Shapes for the self-check, not part of the product.</summary>
        private static class Sample
        {
            public static void Process(double value)
            {
            }

            public static void ProcessBlock(float[] values)
            {
            }
        }

        /// <summary>Whether a method takes one scalar sample rather than something block-shaped.</summary>
        private static bool IsPerSample(MethodInfo method)
        {
            ParameterInfo[] parameters = method.GetParameters();

            if (parameters.Length == 0)
            {
                return false;
            }

            foreach (ParameterInfo parameter in parameters)
            {
                Type type = parameter.ParameterType;

                if (type.IsArray ||
                    type.IsGenericType ||
                    type.Name.IndexOf("Block", StringComparison.Ordinal) >= 0 ||
                    type.Name.IndexOf("Frame", StringComparison.Ordinal) >= 0 ||
                    type.Name.IndexOf("Span", StringComparison.Ordinal) >= 0)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
