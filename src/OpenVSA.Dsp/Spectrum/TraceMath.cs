using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenVSA.Core;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// One trace-math operation (<c>REQ-DSP-046</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stated per point rather than per trace, so that an operator carries only its arithmetic and
    /// none of the axis checking, register handling or frame construction around it. That is what
    /// makes the requirement's extensibility real: a new operator is an implementation of this
    /// interface and a call to <see cref="TraceMath.Register"/>, with nothing in the dispatch to
    /// change.
    /// </para>
    /// <para>
    /// Operands are the calibrated complex spectrum of <c>REQ-TRC-001</c>, in volts, so the same
    /// operator serves whichever display format the result is later rendered in.
    /// </para>
    /// </remarks>
    public interface ITraceOperator
    {
        /// <summary>The operator's name, as it is selected by.</summary>
        string Name { get; }

        /// <summary>Whether this operator needs a second operand.</summary>
        bool TakesTwoOperands { get; }

        /// <summary>
        /// Whether a result of this operator still carries meaningful phase.
        /// </summary>
        /// <remarks>
        /// False for anything that discards it — a magnitude, for instance — so that
        /// <c>REQ-TRC-002</c> makes the phase formats unselectable for the result rather than
        /// showing a phase of zero as though it had been measured.
        /// </remarks>
        bool PreservesPhase { get; }

        /// <summary>Applies the operator to one point.</summary>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand, or <see cref="Complex32.Zero"/> if unary.</param>
        Complex32 Apply(Complex32 left, Complex32 right);
    }

    /// <summary>
    /// Raised when traces cannot be combined because their frequency axes do not correspond.
    /// </summary>
    /// <remarks>
    /// A named type rather than a bare <see cref="ArgumentException"/>, because
    /// <c>REQ-DSP-046</c> requires incommensurate traces to be rejected by name rather than
    /// combined by index — subtracting a 10 MHz span from a 1 MHz one point by point produces a
    /// plausible-looking trace that means nothing at all, and is the failure this exists to make
    /// impossible.
    /// </remarks>
    [Serializable]
    public class IncommensurableTracesException : InvalidOperationException
    {
        /// <summary>Creates the exception.</summary>
        public IncommensurableTracesException()
            : base("The traces do not share a frequency axis.")
        {
        }

        /// <summary>Creates the exception with a message.</summary>
        /// <param name="message">What differs between the axes.</param>
        public IncommensurableTracesException(string message)
            : base(message)
        {
        }

        /// <summary>Creates the exception with a message and an inner exception.</summary>
        /// <param name="message">What differs between the axes.</param>
        /// <param name="inner">The underlying cause.</param>
        public IncommensurableTracesException(string message, Exception inner)
            : base(message, inner)
        {
        }

        /// <summary>Deserialisation constructor.</summary>
        /// <param name="info">Serialisation data.</param>
        /// <param name="context">Streaming context.</param>
        protected IncommensurableTracesException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// Trace math: operators, the dispatch that applies them, and the axis check
    /// (<c>REQ-DSP-046</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Dispatch is a registry, not a switch.</strong> The requirement asks for the operator
    /// set to be extensible and for that to be demonstrable — so adding one must not touch the
    /// code that applies them. Everything below <see cref="Apply(string, SpectrumFrame,
    /// SpectrumFrame)"/> is written against <see cref="ITraceOperator"/> and knows nothing about
    /// which operators exist.
    /// </para>
    /// <para>
    /// <strong>Division by zero produces non-finite values rather than an exception or a
    /// zero.</strong> A zero bin in the divisor is a perfectly ordinary thing to meet — it is what
    /// an unfilled bin of a stored reference trace holds — and stopping the measurement for it, or
    /// quietly returning zero as though the ratio were known, are both worse than saying so.
    /// <c>REQ-UI-032</c>'s <c>NAN</c> and <c>INF</c> readouts are how it is said: <c>0/0</c> is
    /// undefined and gives NaN, and anything else over zero overflows and gives an infinity.
    /// </para>
    /// </remarks>
    public static class TraceMath
    {
        private static readonly ConcurrentDictionary<string, ITraceOperator> Registry =
            new ConcurrentDictionary<string, ITraceOperator>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Tolerance on axis agreement, as a fraction of the bin width.</summary>
        /// <remarks>
        /// Not exact equality: two frames of the same measurement can differ in the last bit of a
        /// start frequency that was arrived at by different arithmetic, and refusing to combine
        /// those would make the facility unusable for the case it is most wanted in. A thousandth
        /// of a bin is far below any misalignment that could mislead.
        /// </remarks>
        public const double AxisTolerance = 1e-3;

        static TraceMath()
        {
            Register(new AddOperator());
            Register(new SubtractOperator());
            Register(new MultiplyOperator());
            Register(new DivideOperator());
            Register(new MagnitudeOperator());
            Register(new ConjugateOperator());
        }

        /// <summary>The registered operators, in name order.</summary>
        public static IReadOnlyList<ITraceOperator> Operators =>
            Registry.Values.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>
        /// Adds an operator to the dispatch.
        /// </summary>
        /// <param name="op">The operator; its name must not already be registered.</param>
        /// <exception cref="ArgumentNullException"><paramref name="op"/> or its name is null.</exception>
        /// <exception cref="ArgumentException">The name is already registered.</exception>
        /// <remarks>
        /// A duplicate name is refused rather than replacing what is there. Silently shadowing an
        /// operator would change the meaning of every trace already computed with it, with nothing
        /// on screen to say so.
        /// </remarks>
        public static void Register(ITraceOperator op)
        {
            if (op == null)
            {
                throw new ArgumentNullException(nameof(op));
            }

            if (string.IsNullOrEmpty(op.Name))
            {
                throw new ArgumentNullException(nameof(op), "An operator needs a name.");
            }

            if (!Registry.TryAdd(op.Name, op))
            {
                throw new ArgumentException(
                    "An operator named '" + op.Name + "' is already registered.", nameof(op));
            }
        }

        /// <summary>Whether an operator of that name is registered.</summary>
        /// <param name="name">The operator name; case-insensitive.</param>
        public static bool Contains(string name) =>
            !string.IsNullOrEmpty(name) && Registry.ContainsKey(name);

        /// <summary>
        /// Looks an operator up by name.
        /// </summary>
        /// <param name="name">The operator name; case-insensitive.</param>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is null or empty.</exception>
        /// <exception cref="KeyNotFoundException">No such operator is registered.</exception>
        public static ITraceOperator Get(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            ITraceOperator op;

            if (!Registry.TryGetValue(name, out op))
            {
                throw new KeyNotFoundException(
                    "No trace-math operator named '" + name + "'. Registered: " +
                    string.Join(", ", Operators.Select(o => o.Name)) + ".");
            }

            return op;
        }

        /// <summary>
        /// Applies an operator to two traces, point by point.
        /// </summary>
        /// <param name="name">The operator name; case-insensitive.</param>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns>A new frame on the left operand's axis.</returns>
        /// <exception cref="ArgumentNullException">An operand is null.</exception>
        /// <exception cref="KeyNotFoundException">No such operator is registered.</exception>
        /// <exception cref="ArgumentException">The operator is unary.</exception>
        /// <exception cref="IncommensurableTracesException">The axes do not correspond.</exception>
        public static SpectrumFrame Apply(string name, SpectrumFrame left, SpectrumFrame right) =>
            Apply(Get(name), left, right);

        /// <summary>
        /// Applies an operator to two traces, point by point.
        /// </summary>
        /// <param name="op">The operator.</param>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        /// <returns>A new frame on the left operand's axis.</returns>
        /// <exception cref="ArgumentNullException">The operator or an operand is null.</exception>
        /// <exception cref="ArgumentException">The operator is unary.</exception>
        /// <exception cref="IncommensurableTracesException">The axes do not correspond.</exception>
        public static SpectrumFrame Apply(
            ITraceOperator op, SpectrumFrame left, SpectrumFrame right)
        {
            if (op == null)
            {
                throw new ArgumentNullException(nameof(op));
            }

            if (left == null)
            {
                throw new ArgumentNullException(nameof(left));
            }

            if (right == null)
            {
                throw new ArgumentNullException(nameof(right));
            }

            if (!op.TakesTwoOperands)
            {
                throw new ArgumentException(
                    "'" + op.Name + "' takes one operand; use the trace or constant overload.",
                    nameof(op));
            }

            RequireCommensurate(left, right);

            ReadOnlySpan<float> a = left.Complex;
            ReadOnlySpan<float> b = right.Complex;
            var result = new float[a.Length];

            for (int i = 0; i < left.PointCount; i++)
            {
                Complex32 value = op.Apply(
                    new Complex32(a[i * 2], a[i * 2 + 1]),
                    new Complex32(b[i * 2], b[i * 2 + 1]));

                result[i * 2] = value.I;
                result[i * 2 + 1] = value.Q;
            }

            return Frame(left, result, op);
        }

        /// <summary>
        /// Applies an operator to a trace and a constant, point by point.
        /// </summary>
        /// <param name="name">The operator name; case-insensitive.</param>
        /// <param name="left">The trace.</param>
        /// <param name="constant">The constant, in volts.</param>
        /// <returns>A new frame on the trace's axis.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="left"/> is null.</exception>
        /// <exception cref="KeyNotFoundException">No such operator is registered.</exception>
        public static SpectrumFrame Apply(string name, SpectrumFrame left, Complex32 constant) =>
            Apply(Get(name), left, constant);

        /// <summary>
        /// Applies an operator to a trace and a constant, point by point.
        /// </summary>
        /// <param name="op">The operator.</param>
        /// <param name="left">The trace.</param>
        /// <param name="constant">The constant, in volts. Ignored by a unary operator.</param>
        /// <returns>A new frame on the trace's axis.</returns>
        /// <exception cref="ArgumentNullException">The operator or the trace is null.</exception>
        /// <remarks>
        /// A unary operator is applied through here too, with the constant ignored, so that the
        /// caller does not have to know an operator's arity in order to invoke it.
        /// </remarks>
        public static SpectrumFrame Apply(
            ITraceOperator op, SpectrumFrame left, Complex32 constant)
        {
            if (op == null)
            {
                throw new ArgumentNullException(nameof(op));
            }

            if (left == null)
            {
                throw new ArgumentNullException(nameof(left));
            }

            ReadOnlySpan<float> a = left.Complex;
            var result = new float[a.Length];

            for (int i = 0; i < left.PointCount; i++)
            {
                Complex32 value = op.Apply(new Complex32(a[i * 2], a[i * 2 + 1]), constant);

                result[i * 2] = value.I;
                result[i * 2 + 1] = value.Q;
            }

            return Frame(left, result, op);
        }

        /// <summary>
        /// Whether two traces share a frequency axis closely enough to be combined.
        /// </summary>
        /// <param name="left">One trace.</param>
        /// <param name="right">The other.</param>
        /// <exception cref="ArgumentNullException">Either is null.</exception>
        public static bool AreCommensurate(SpectrumFrame left, SpectrumFrame right)
        {
            if (left == null)
            {
                throw new ArgumentNullException(nameof(left));
            }

            if (right == null)
            {
                throw new ArgumentNullException(nameof(right));
            }

            return Difference(left, right) == null;
        }

        /// <summary>
        /// Throws unless two traces share a frequency axis.
        /// </summary>
        /// <param name="left">One trace.</param>
        /// <param name="right">The other.</param>
        /// <exception cref="ArgumentNullException">Either is null.</exception>
        /// <exception cref="IncommensurableTracesException">The axes do not correspond.</exception>
        public static void RequireCommensurate(SpectrumFrame left, SpectrumFrame right)
        {
            if (left == null)
            {
                throw new ArgumentNullException(nameof(left));
            }

            if (right == null)
            {
                throw new ArgumentNullException(nameof(right));
            }

            string difference = Difference(left, right);

            if (difference != null)
            {
                throw new IncommensurableTracesException(
                    "The traces cannot be combined: " + difference + ".");
            }
        }

        /// <summary>What differs between two axes, or <c>null</c> if nothing does.</summary>
        private static string Difference(SpectrumFrame left, SpectrumFrame right)
        {
            if (left.PointCount != right.PointCount)
            {
                return "one has " + left.PointCount.ToString(CultureInfo.CurrentCulture) +
                    " points and the other " +
                    right.PointCount.ToString(CultureInfo.CurrentCulture);
            }

            double tolerance = AxisTolerance * Math.Min(left.BinWidthHz, right.BinWidthHz);

            if (Math.Abs(left.BinWidthHz - right.BinWidthHz) > tolerance)
            {
                return "the bin widths differ, " +
                    left.BinWidthHz.ToString("G6", CultureInfo.CurrentCulture) + " Hz against " +
                    right.BinWidthHz.ToString("G6", CultureInfo.CurrentCulture) + " Hz";
            }

            if (Math.Abs(left.StartFrequencyHz - right.StartFrequencyHz) > tolerance)
            {
                return "the axes start at different frequencies, " +
                    left.StartFrequencyHz.ToString("G9", CultureInfo.CurrentCulture) +
                    " Hz against " +
                    right.StartFrequencyHz.ToString("G9", CultureInfo.CurrentCulture) + " Hz";
            }

            return null;
        }

        /// <summary>Builds the result frame on the left operand's axis.</summary>
        /// <remarks>
        /// The average count is carried across but not combined: a difference of two ten-average
        /// traces is not a twenty-average anything, and the left operand's provenance is the least
        /// misleading thing to keep.
        /// </remarks>
        private static SpectrumFrame Frame(
            SpectrumFrame left, float[] result, ITraceOperator op) =>
            left.WithComplex(
                result,
                op.PreservesPhase && left.HasPhase,
                left.AverageCount,
                left.EffectiveAverageCount);

        // ---- The operators ---------------------------------------------------------------------

        private sealed class AddOperator : ITraceOperator
        {
            public string Name => "Add";

            public bool TakesTwoOperands => true;

            public bool PreservesPhase => true;

            public Complex32 Apply(Complex32 left, Complex32 right) =>
                new Complex32(left.I + right.I, left.Q + right.Q);
        }

        private sealed class SubtractOperator : ITraceOperator
        {
            public string Name => "Subtract";

            public bool TakesTwoOperands => true;

            public bool PreservesPhase => true;

            public Complex32 Apply(Complex32 left, Complex32 right) =>
                new Complex32(left.I - right.I, left.Q - right.Q);
        }

        private sealed class MultiplyOperator : ITraceOperator
        {
            public string Name => "Multiply";

            public bool TakesTwoOperands => true;

            public bool PreservesPhase => true;

            public Complex32 Apply(Complex32 left, Complex32 right) =>
                new Complex32(
                    (float)((double)left.I * right.I - (double)left.Q * right.Q),
                    (float)((double)left.I * right.Q + (double)left.Q * right.I));
        }

        private sealed class DivideOperator : ITraceOperator
        {
            public string Name => "Divide";

            public bool TakesTwoOperands => true;

            public bool PreservesPhase => true;

            public Complex32 Apply(Complex32 left, Complex32 right)
            {
                double denominator = right.MagnitudeSquared;

                if (denominator == 0.0)
                {
                    // REQ-UI-032's readouts: 0/0 is undefined and reads NAN, anything else over
                    // zero overflows and reads INF. Neither is an error - a zero bin in a divisor
                    // is ordinary - so nothing is thrown and nothing is quietly returned as zero.
                    return new Complex32(OverZero(left.I), OverZero(left.Q));
                }

                return new Complex32(
                    (float)(((double)left.I * right.I + (double)left.Q * right.Q) / denominator),
                    (float)(((double)left.Q * right.I - (double)left.I * right.Q) / denominator));
            }

            private static float OverZero(float numerator)
            {
                if (numerator == 0.0f)
                {
                    return float.NaN;
                }

                return numerator > 0.0f ? float.PositiveInfinity : float.NegativeInfinity;
            }
        }

        private sealed class MagnitudeOperator : ITraceOperator
        {
            public string Name => "Magnitude";

            public bool TakesTwoOperands => false;

            /// <summary>Magnitude discards phase, and no later step can recover it.</summary>
            public bool PreservesPhase => false;

            public Complex32 Apply(Complex32 left, Complex32 right) =>
                new Complex32((float)left.Magnitude, 0.0f);
        }

        private sealed class ConjugateOperator : ITraceOperator
        {
            public string Name => "Conjugate";

            public bool TakesTwoOperands => false;

            public bool PreservesPhase => true;

            public Complex32 Apply(Complex32 left, Complex32 right) =>
                new Complex32(left.I, -left.Q);
        }
    }
}
