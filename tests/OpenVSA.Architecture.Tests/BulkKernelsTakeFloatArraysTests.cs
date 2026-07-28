using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenVSA.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Architecture.Tests
{
    /// <summary>
    /// <c>REQ-DAT-003</c>: no bulk kernel takes <c>Complex32[]</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Complex32"/> exists so that a <c>float[2N]</c> and a <c>Complex32[N]</c> describe
    /// the same bytes, and it is the right type at an API boundary where a caller reasons about
    /// complex samples. It is the wrong type inside a loop: on .NET Framework the portable
    /// <c>Span&lt;T&gt;</c> has no JIT intrinsic, a struct element defeats the bounds-check elision
    /// a raw array gets, and <c>Vector&lt;float&gt;</c> cannot load from it at all.
    /// </para>
    /// <para>
    /// The layout half of this requirement is covered by <c>Complex32Tests</c>. This is the other
    /// half, and it is the half that decays: a kernel written against <c>Complex32[]</c> reads
    /// better, passes every correctness test, and is slower in a way no functional test notices.
    /// </para>
    /// </remarks>
    public class BulkKernelsTakeFloatArraysTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the examined method count is written.</param>
        public BulkKernelsTakeFloatArraysTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void NoPublicDspMethodTakesABulkComplex32Buffer()
        {
            Assembly dsp = Assembly.Load("OpenVSA.Dsp");

            var offenders = new List<string>();
            int examined = 0;

            foreach (Type type in dsp.GetExportedTypes())
            {
                foreach (MethodInfo method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    examined++;

                    foreach (ParameterInfo parameter in method.GetParameters())
                    {
                        if (IsBulkComplex32(parameter.ParameterType))
                        {
                            offenders.Add(
                                type.Name + "." + method.Name + "(" + parameter.Name + ": " +
                                parameter.ParameterType.Name + ")");
                        }
                    }
                }
            }

            _output.WriteLine(examined + " public DSP methods examined");

            Assert.True(examined > 100, "Only " + examined + " methods were examined.");

            Assert.False(
                offenders.Any(),
                "REQ-DAT-003: a bulk kernel takes Complex32 rather than float. On .NET Framework " +
                "that costs the bounds-check elision and the vector load a raw float[] gets, in a " +
                "way no correctness test notices." + Environment.NewLine +
                string.Join(Environment.NewLine, offenders.Distinct()));
        }

        [Fact]
        public void TheRuleIsAboutBuffersAndNotAboutSingleValues()
        {
            // A single Complex32 by value is exactly what the type is for, and forbidding it would
            // make the check fire on the API boundary it exists to serve. The rule is buffers only.
            Assert.False(IsBulkComplex32(typeof(Complex32)));
            Assert.False(IsBulkComplex32(typeof(float[])));

            Assert.True(IsBulkComplex32(typeof(Complex32[])));
            Assert.True(IsBulkComplex32(typeof(Span<Complex32>)));
            Assert.True(IsBulkComplex32(typeof(ReadOnlySpan<Complex32>)));
            Assert.True(IsBulkComplex32(typeof(IList<Complex32>)));
        }

        /// <summary>Whether a parameter type is a sequence of <see cref="Complex32"/>.</summary>
        private static bool IsBulkComplex32(Type type)
        {
            if (type == null)
            {
                return false;
            }

            if (type.IsByRef || type.IsPointer)
            {
                return IsBulkComplex32(type.GetElementType());
            }

            if (type.IsArray)
            {
                return type.GetElementType() == typeof(Complex32);
            }

            // Span<Complex32>, ReadOnlySpan<Complex32>, IEnumerable<Complex32> and friends.
            return type.IsGenericType &&
                   type.GetGenericArguments().Any(a => a == typeof(Complex32));
        }
    }
}
