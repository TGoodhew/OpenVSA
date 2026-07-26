using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-DSP-046</c>: trace math, its axis check, and its registers.
    /// </summary>
    public class TraceMathTests
    {
        private readonly ITestOutputHelper _output;

        public TraceMathTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void EveryOperatorTheRequirementNamesIsAvailable()
        {
            string[] required =
            {
                "Add", "Subtract", "Multiply", "Divide", "Magnitude", "Conjugate",
            };

            IReadOnlyList<ITraceOperator> registered = TraceMath.Operators;

            foreach (string name in required)
            {
                Assert.True(
                    TraceMath.Contains(name),
                    "The requirement names '" + name + "' but it is not registered. Registered: " +
                    string.Join(", ", registered.Select(o => o.Name)) + ".");
            }
        }

        [Theory]
        [InlineData("Add")]
        [InlineData("Subtract")]
        [InlineData("Multiply")]
        [InlineData("Divide")]
        public void EachBinaryOperatorMatchesTheArithmeticDoneDirectly(string name)
        {
            // The requirement's criterion: each operator checked against the arithmetic performed
            // on the underlying data, rather than against itself.
            SpectrumFrame left = Frame(new[] { 3.0f, 4.0f, -2.0f, 1.0f, 0.5f, -0.25f });
            SpectrumFrame right = Frame(new[] { 1.0f, -2.0f, 0.5f, 0.5f, 2.0f, 1.0f });

            SpectrumFrame result = TraceMath.Apply(name, left, right);

            for (int i = 0; i < left.PointCount; i++)
            {
                var a = new System.Numerics.Complex(left.Complex[i * 2], left.Complex[i * 2 + 1]);
                var b = new System.Numerics.Complex(right.Complex[i * 2], right.Complex[i * 2 + 1]);

                System.Numerics.Complex expected;

                switch (name)
                {
                    case "Add": expected = a + b; break;
                    case "Subtract": expected = a - b; break;
                    case "Multiply": expected = a * b; break;
                    default: expected = a / b; break;
                }

                Assert.Equal(expected.Real, result.Complex[i * 2], 5);
                Assert.Equal(expected.Imaginary, result.Complex[i * 2 + 1], 5);
            }
        }

        [Fact]
        public void MagnitudeAndConjugateMatchTheArithmeticDoneDirectly()
        {
            SpectrumFrame trace = Frame(new[] { 3.0f, 4.0f, -2.0f, 1.0f, 0.0f, -0.25f });

            SpectrumFrame magnitude = TraceMath.Apply("Magnitude", trace, Complex32.Zero);
            SpectrumFrame conjugate = TraceMath.Apply("Conjugate", trace, Complex32.Zero);

            for (int i = 0; i < trace.PointCount; i++)
            {
                float re = trace.Complex[i * 2];
                float im = trace.Complex[i * 2 + 1];

                Assert.Equal(Math.Sqrt(re * (double)re + im * (double)im), magnitude.Complex[i * 2], 5);
                Assert.Equal(0.0f, magnitude.Complex[i * 2 + 1]);

                Assert.Equal(re, conjugate.Complex[i * 2]);
                Assert.Equal(-im, conjugate.Complex[i * 2 + 1]);
            }
        }

        [Fact]
        public void MagnitudeDiscardsPhaseAndTheResultSaysSo()
        {
            // REQ-TRC-002 reads this to make the phase formats unselectable, rather than showing a
            // phase of zero as though it had been measured.
            SpectrumFrame trace = Frame(new[] { 3.0f, 4.0f });

            Assert.True(trace.HasPhase);
            Assert.False(TraceMath.Apply("Magnitude", trace, Complex32.Zero).HasPhase);
            Assert.True(TraceMath.Apply("Conjugate", trace, Complex32.Zero).HasPhase);
        }

        [Theory]
        [InlineData("Add")]
        [InlineData("Subtract")]
        [InlineData("Multiply")]
        [InlineData("Divide")]
        public void EachBinaryOperatorTakesAConstantAsWellAsATrace(string name)
        {
            // "trace/trace and trace/constant" - the same operator, applied against a scalar.
            SpectrumFrame trace = Frame(new[] { 3.0f, 4.0f, -2.0f, 1.0f });
            var constant = new Complex32(2.0f, -1.0f);

            SpectrumFrame result = TraceMath.Apply(name, trace, constant);

            var b = new System.Numerics.Complex(constant.I, constant.Q);

            for (int i = 0; i < trace.PointCount; i++)
            {
                var a = new System.Numerics.Complex(trace.Complex[i * 2], trace.Complex[i * 2 + 1]);

                System.Numerics.Complex expected;

                switch (name)
                {
                    case "Add": expected = a + b; break;
                    case "Subtract": expected = a - b; break;
                    case "Multiply": expected = a * b; break;
                    default: expected = a / b; break;
                }

                Assert.Equal(expected.Real, result.Complex[i * 2], 5);
                Assert.Equal(expected.Imaginary, result.Complex[i * 2 + 1], 5);
            }
        }

        [Fact]
        public void DividingByZeroReadsNanOrInfRatherThanThrowingOrReadingZero()
        {
            // REQ-DSP-046 by way of REQ-UI-032. A zero bin in a divisor is ordinary - it is what an
            // unfilled bin of a stored reference holds - so it is neither an error nor a ratio of
            // nought. Zero over zero is undefined; anything else over zero overflows.
            SpectrumFrame numerator = Frame(new[] { 0.0f, 0.0f, 2.0f, 0.0f, -2.0f, 0.0f });
            SpectrumFrame zero = Frame(new[] { 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f });

            SpectrumFrame result = TraceMath.Apply("Divide", numerator, zero);

            Assert.True(float.IsNaN(result.Complex[0]));
            Assert.True(float.IsPositiveInfinity(result.Complex[2]));
            Assert.True(float.IsNegativeInfinity(result.Complex[4]));

            // And the same for a constant divisor of zero.
            SpectrumFrame byConstant = TraceMath.Apply("Divide", numerator, Complex32.Zero);

            Assert.True(float.IsNaN(byConstant.Complex[0]));
            Assert.True(float.IsPositiveInfinity(byConstant.Complex[2]));
        }

        [Fact]
        public void TracesOnDifferentAxesAreRejectedByNameRatherThanCombinedByIndex()
        {
            // The failure this check exists to prevent: subtracting a 1 kHz-per-bin trace from a
            // 2 kHz-per-bin one point by point produces something that looks like a measurement and
            // means nothing.
            SpectrumFrame fine = SpectrumFrame.FromComplex(
                new[] { 1.0f, 0.0f, 1.0f, 0.0f }, 1e9, 1e3, WindowType.Uniform, 1.0);

            SpectrumFrame coarse = SpectrumFrame.FromComplex(
                new[] { 1.0f, 0.0f, 1.0f, 0.0f }, 1e9, 2e3, WindowType.Uniform, 1.0);

            SpectrumFrame shifted = SpectrumFrame.FromComplex(
                new[] { 1.0f, 0.0f, 1.0f, 0.0f }, 1e9 + 5e3, 1e3, WindowType.Uniform, 1.0);

            SpectrumFrame longer = SpectrumFrame.FromComplex(
                new[] { 1.0f, 0.0f, 1.0f, 0.0f, 1.0f, 0.0f }, 1e9, 1e3, WindowType.Uniform, 1.0);

            foreach (SpectrumFrame other in new[] { coarse, shifted, longer })
            {
                IncommensurableTracesException failure =
                    Assert.Throws<IncommensurableTracesException>(
                        () => TraceMath.Apply("Subtract", fine, other));

                _output.WriteLine(failure.Message);
                Assert.False(TraceMath.AreCommensurate(fine, other));
            }

            Assert.True(TraceMath.AreCommensurate(fine, fine));
        }

        [Fact]
        public void AxesThatAgreeToWithinRoundingAreStillCombined()
        {
            // The other side of the same check. Two frames of one measurement can differ in the
            // last bit of a start frequency reached by different arithmetic, and refusing those
            // would make the facility useless for the case it is most wanted in.
            SpectrumFrame first = SpectrumFrame.FromComplex(
                new[] { 1.0f, 0.0f, 1.0f, 0.0f }, 1e9, 1e3, WindowType.Uniform, 1.0);

            SpectrumFrame second = SpectrumFrame.FromComplex(
                new[] { 1.0f, 0.0f, 1.0f, 0.0f }, 1e9 + 1e-6, 1e3, WindowType.Uniform, 1.0);

            Assert.True(TraceMath.AreCommensurate(first, second));
            Assert.Equal(2.0f, TraceMath.Apply("Add", first, second).Complex[0], 5);
        }

        [Fact]
        public void ARegisterSurvivesStoreAndRecallWithBitIdenticalValues()
        {
            // The requirement's criterion, checked bit by bit rather than to a tolerance: a stored
            // trace that came back nearly the same would be a trace that had been through an
            // arithmetic it was never asked for.
            var registers = new TraceRegisters();
            SpectrumFrame stored = Frame(new[] { 3.0f, 4.0f, -2.5f, 0.125f, 1e-30f, -1e30f });

            Assert.False(registers.IsOccupied(3));
            Assert.Null(registers.Recall(3));

            registers.Store(3, stored);

            Assert.True(registers.IsOccupied(3));

            SpectrumFrame recalled = registers.Recall(3);

            Assert.Equal(stored.PointCount, recalled.PointCount);

            for (int i = 0; i < stored.Complex.Length; i++)
            {
                Assert.Equal(
                    BitConverter.GetBytes(stored.Complex[i]),
                    BitConverter.GetBytes(recalled.Complex[i]));
            }
        }

        [Fact]
        public void RegistersAreNamedAndBoundedAndClearable()
        {
            var registers = new TraceRegisters(4);

            Assert.Equal(4, registers.Count);
            Assert.Equal("D1", registers.NameOf(1));
            Assert.Equal("D4", registers.NameOf(4));

            registers.Store(2, Frame(new[] { 1.0f, 0.0f }));
            Assert.True(registers.IsOccupied(2));

            registers.Clear();
            Assert.False(registers.IsOccupied(2));

            Assert.Throws<ArgumentOutOfRangeException>(() => registers.Recall(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => registers.Recall(5));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TraceRegisters(0));
        }

        [Fact]
        public void AnOperatorCanBeAddedWithoutTouchingTheDispatch()
        {
            // The extensibility the requirement asks to see demonstrated. This operator is declared
            // here, in the test assembly, and applied through exactly the same entry point as the
            // built-in ones - so nothing in the dispatch knows it exists.
            const string name = "Halve";

            if (!TraceMath.Contains(name))
            {
                TraceMath.Register(new HalveOperator(name));
            }

            SpectrumFrame trace = Frame(new[] { 3.0f, 4.0f, -2.0f, 1.0f });
            SpectrumFrame result = TraceMath.Apply(name, trace, Complex32.Zero);

            Assert.Equal(1.5f, result.Complex[0], 5);
            Assert.Equal(2.0f, result.Complex[1], 5);
            Assert.Equal(-1.0f, result.Complex[2], 5);

            Assert.Contains(TraceMath.Operators, o => o.Name == name);
        }

        [Fact]
        public void TheArgumentsAreChecked()
        {
            SpectrumFrame trace = Frame(new[] { 1.0f, 0.0f });

            Assert.Throws<ArgumentNullException>(() => TraceMath.Register(null));
            Assert.Throws<ArgumentNullException>(() => TraceMath.Get(null));
            Assert.Throws<KeyNotFoundException>(() => TraceMath.Get("Exponentiate"));
            Assert.Throws<ArgumentNullException>(() => TraceMath.Apply("Add", null, trace));
            Assert.Throws<ArgumentNullException>(() => TraceMath.Apply("Add", trace, (SpectrumFrame)null));
            Assert.Throws<ArgumentNullException>(
                () => TraceMath.Apply((ITraceOperator)null, trace, trace));

            // A unary operator applied as a binary one is a mistake worth naming; the same operator
            // applied through the constant overload is not, and the constant is simply ignored.
            Assert.Throws<ArgumentException>(() => TraceMath.Apply("Magnitude", trace, trace));
            Assert.NotNull(TraceMath.Apply("Magnitude", trace, new Complex32(9.0f, 9.0f)));

            // A duplicate name is refused rather than shadowing what is there - which would change
            // the meaning of every trace already computed with the operator it replaced.
            Assert.Throws<ArgumentException>(() => TraceMath.Register(new HalveOperator("Add")));
        }

        /// <summary>Halves a trace. Declared here to show an operator can be added from outside.</summary>
        private sealed class HalveOperator : ITraceOperator
        {
            public HalveOperator(string name)
            {
                Name = name;
            }

            public string Name { get; }

            public bool TakesTwoOperands => false;

            public bool PreservesPhase => true;

            public Complex32 Apply(Complex32 left, Complex32 right) =>
                new Complex32(left.I / 2.0f, left.Q / 2.0f);
        }

        private static SpectrumFrame Frame(float[] complex) =>
            SpectrumFrame.FromComplex(complex, 1e9, 1e3, WindowType.Uniform, 1.0);
    }
}
