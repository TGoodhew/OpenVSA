using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using OpenVSA.Demod.Chain;
using OpenVSA.Dsp.Fft;

namespace OpenVSA.Demod.Results
{
    /// <summary>The trace data sources a demodulation offers (<c>REQ-DEM-080</c>).</summary>
    public enum ResultTrace
    {
        /// <summary>The measured complex waveform.</summary>
        IqMeasuredTime = 0,

        /// <summary>The regenerated ideal complex waveform.</summary>
        IqReferenceTime,

        /// <summary>Symbol-instant points in the I/Q plane, and nothing between them.</summary>
        Constellation,

        /// <summary>The same points with the inter-symbol trajectory between them.</summary>
        IqVector,

        /// <summary>The in-phase component, for folding on the symbol clock.</summary>
        EyeI,

        /// <summary>The quadrature component, for folding on the symbol clock.</summary>
        EyeQ,

        /// <summary>Phase versus time, for folding on the symbol clock.</summary>
        Trellis,

        /// <summary>The error vector's magnitude at each symbol.</summary>
        ErrorVectorTime,

        /// <summary>The spectrum of the error vector sequence.</summary>
        ErrorVectorSpectrum,

        /// <summary>Magnitude error at each symbol.</summary>
        MagnitudeError,

        /// <summary>Phase error at each symbol.</summary>
        PhaseError,

        /// <summary>The detected symbols and bits, as text.</summary>
        SymbolTable,

        /// <summary>The metrics of section 11.7, as text.</summary>
        ErrorSummary,

        /// <summary>The equaliser's coefficients.</summary>
        EqualiserImpulseResponse,

        /// <summary>The channel's estimated magnitude and phase response.</summary>
        ChannelFrequencyResponse,
    }

    /// <summary>What a trace's horizontal axis counts.</summary>
    public enum ResultTraceDomain
    {
        /// <summary>Samples of the result window.</summary>
        Sample = 0,

        /// <summary>Symbols of the result.</summary>
        Symbol,

        /// <summary>Frequency, in hertz.</summary>
        Frequency,

        /// <summary>The I/Q plane; the values are positions, not a series.</summary>
        IqPlane,

        /// <summary>Lines of text.</summary>
        Text,
    }

    /// <summary>One trace's data, as the displays take it.</summary>
    public sealed class ResultTraceData
    {
        internal ResultTraceData(
            ResultTrace trace,
            ResultTraceDomain domain,
            bool isComplex,
            IList<double> values,
            double xStart,
            double xStep,
            string unit,
            IList<string> text,
            double foldSamplesPerSymbol)
        {
            Trace = trace;
            Domain = domain;
            IsComplex = isComplex;
            Values = new ReadOnlyCollection<double>(values ?? new List<double>());
            XStart = xStart;
            XStep = xStep;
            Unit = unit ?? string.Empty;
            Text = new ReadOnlyCollection<string>(text ?? new List<string>());
            FoldSamplesPerSymbol = foldSamplesPerSymbol;
        }

        /// <summary>Which trace this is.</summary>
        public ResultTrace Trace { get; }

        /// <summary>What the horizontal axis counts.</summary>
        public ResultTraceDomain Domain { get; }

        /// <summary>Whether <see cref="Values"/> is interleaved pairs rather than scalars.</summary>
        public bool IsComplex { get; }

        /// <summary>The data; interleaved real and imaginary when <see cref="IsComplex"/>.</summary>
        public IReadOnlyList<double> Values { get; }

        /// <summary>The first point's position on the horizontal axis.</summary>
        public double XStart { get; }

        /// <summary>The step between points on the horizontal axis.</summary>
        public double XStep { get; }

        /// <summary>The values' unit, where they have one.</summary>
        public string Unit { get; }

        /// <summary>The lines, for a trace whose domain is <see cref="ResultTraceDomain.Text"/>.</summary>
        public IReadOnlyList<string> Text { get; }

        /// <summary>
        /// The period a folded display folds on, in samples, or zero when the trace is not folded.
        /// </summary>
        /// <remarks>
        /// The eye and the trellis are the same samples as the measured waveform, drawn folded on
        /// the symbol clock. Folding is the display's (<c>REQ-UI-051</c>); what the data source owes
        /// it is the period to fold on, and the symbol clock is not generally a whole number of
        /// samples once a signal has been resampled.
        /// </remarks>
        public double FoldSamplesPerSymbol { get; }

        /// <summary>How many points there are, pairs counting as one.</summary>
        public int Count => IsComplex ? Values.Count / 2 : Values.Count;

        /// <inheritdoc />
        public override string ToString() =>
            Trace + ": " + Count + " point(s), " + Domain +
            (Unit.Length == 0 ? string.Empty : ", " + Unit);
    }

    /// <summary>
    /// The catalogue of trace data sources a demodulation produces (<c>REQ-DEM-080</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Data sources, not displays.</strong> Each entry here answers "what would this trace
    /// be drawn from", and the drawing belongs to <c>REQ-UI-050</c> and its neighbours. That is why
    /// the eye and the trellis are components and a fold period rather than a folded picture, and
    /// why the constellation and the IQ vector return the same points: <c>REQ-UI-050</c> says in so
    /// many words that the IQ/Vector format is "the same data <em>with</em> the inter-symbol
    /// trajectory", so the difference between them is what is drawn between the points and not what
    /// the points are. <see cref="ResultTraceData.Domain"/> carries that difference —
    /// <c>Constellation</c> is points in the plane, <c>IqVector</c> is a series through them.
    /// </para>
    /// <para>
    /// <strong>Unavailable is not empty.</strong> The equaliser's traces do not exist when the
    /// equaliser has not run, and <see cref="Take"/> refuses them by name.
    /// <c>REQ-DEM-080</c> asks for exactly that distinction: an empty trace is a measurement that
    /// produced nothing, which is a different statement about the signal.
    /// </para>
    /// <para>
    /// <strong>The trellis is <c>[U]</c>.</strong> The requirement marks it as conventional and
    /// cheap rather than confirmed in the reference product, and it is provided on that basis. No
    /// claim of parity is made for it.
    /// </para>
    /// </remarks>
    public static class ResultTraces
    {
        private static readonly ReadOnlyCollection<ResultTrace> Catalogue =
            new ReadOnlyCollection<ResultTrace>(
                new List<ResultTrace>((ResultTrace[])Enum.GetValues(typeof(ResultTrace))));

        /// <summary>Every trace the catalogue lists.</summary>
        public static IReadOnlyList<ResultTrace> All => Catalogue;

        /// <summary>Whether a result can produce a trace.</summary>
        /// <param name="result">The demodulation.</param>
        /// <param name="trace">The trace.</param>
        /// <returns>Whether <see cref="Take"/> would produce data.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
        public static bool IsAvailable(DemodResult result, ResultTrace trace)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            if (result.Trace == null)
            {
                return false;
            }

            switch (trace)
            {
                case ResultTrace.EqualiserImpulseResponse:
                case ResultTrace.ChannelFrequencyResponse:
                    return result.EqualiserCoefficients != null &&
                           result.EqualiserCoefficients.Count > 0;

                case ResultTrace.EyeI:
                case ResultTrace.EyeQ:
                case ResultTrace.Trellis:
                    // An eye is the waveform folded on the symbol clock, so it needs something
                    // between the decisions to fold. At one point a symbol (REQ-DEM-034's lowest
                    // setting) there is nothing there, and the honest answer is that the trace does
                    // not exist rather than a fold of one point per symbol drawn as though it did.
                    return result.Trace.SamplesPerSymbol >= 2;

                default:
                    return true;
            }
        }

        /// <summary>Why a trace is not available, for a display to say so.</summary>
        /// <param name="result">The demodulation.</param>
        /// <param name="trace">The trace.</param>
        /// <returns>The reason, or an empty string when the trace is available.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
        public static string ReasonUnavailable(DemodResult result, ResultTrace trace)
        {
            if (IsAvailable(result, trace))
            {
                return string.Empty;
            }

            if (result.Trace == null)
            {
                return "This demodulation produced no result to draw.";
            }

            if (trace == ResultTrace.EyeI || trace == ResultTrace.EyeQ ||
                trace == ResultTrace.Trellis)
            {
                return "The traces are drawn at one point per symbol, so there is nothing between " +
                    "the decisions to fold an eye on. Raise points per symbol (REQ-DEM-034) and " +
                    "the trace appears; the metrics do not change, because they are computed at " +
                    "the decision instants either way.";
            }

            return "The equaliser did not run, so there are no coefficients and no channel " +
                "estimate. Turn the equaliser on (REQ-DEM-050) and the trace appears.";
        }

        /// <summary>Produces a trace's data.</summary>
        /// <param name="result">The demodulation.</param>
        /// <param name="trace">The trace.</param>
        /// <returns>The data.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// The trace is not available for this result; <see cref="ReasonUnavailable"/> says why.
        /// </exception>
        public static ResultTraceData Take(DemodResult result, ResultTrace trace)
        {
            if (!IsAvailable(result, trace))
            {
                throw new InvalidOperationException(ReasonUnavailable(result, trace));
            }

            SymbolTrace symbols = result.Trace;

            switch (trace)
            {
                case ResultTrace.IqMeasuredTime:
                    return Waveform(trace, Samples(symbols), symbols, string.Empty);

                case ResultTrace.IqReferenceTime:
                    return Waveform(trace, Reference(result), symbols, string.Empty);

                case ResultTrace.Constellation:
                    return Points(trace, ResultTraceDomain.IqPlane, symbols);

                case ResultTrace.IqVector:
                    return Waveform(trace, Samples(symbols), symbols, string.Empty);

                case ResultTrace.EyeI:
                    return Component(trace, symbols, 0);

                case ResultTrace.EyeQ:
                    return Component(trace, symbols, 1);

                case ResultTrace.Trellis:
                    return Trellis(symbols);

                case ResultTrace.ErrorVectorTime:
                    return PerSymbol(trace, ErrorVector(symbols), "%");

                case ResultTrace.ErrorVectorSpectrum:
                    return Spectrum(symbols);

                case ResultTrace.MagnitudeError:
                    return PerSymbol(trace, MagnitudeError(symbols), "%");

                case ResultTrace.PhaseError:
                    return PerSymbol(trace, PhaseError(symbols), "deg");

                case ResultTrace.SymbolTable:
                    return Text(trace, SymbolTableText(symbols));

                case ResultTrace.ErrorSummary:
                    return Text(
                        trace,
                        result.Summary == null
                            ? new List<string>()
                            : new List<string>(result.Summary.Render()));

                case ResultTrace.EqualiserImpulseResponse:
                    return Taps(result);

                default:
                    return Channel(result);
            }
        }

        private static ResultTraceData Waveform(
            ResultTrace trace, IList<double> values, SymbolTrace symbols, string unit) =>
            new ResultTraceData(
                trace,
                trace == ResultTrace.Constellation ? ResultTraceDomain.IqPlane : ResultTraceDomain.Sample,
                true,
                values,
                0.0,
                symbols.SymbolRateHz <= 0.0 ? 1.0 : 1.0 / (symbols.SymbolRateHz * symbols.SamplesPerSymbol),
                unit,
                null,
                symbols.SamplesPerSymbol);

        private static ResultTraceData Points(
            ResultTrace trace, ResultTraceDomain domain, SymbolTrace symbols)
        {
            var values = new List<double>(symbols.SymbolCount * 2);

            foreach (ConstellationPoint point in symbols.Measured)
            {
                values.Add(point.I);
                values.Add(point.Q);
            }

            return new ResultTraceData(trace, domain, true, values, 0.0, 1.0, string.Empty, null, 0.0);
        }

        private static ResultTraceData Component(ResultTrace trace, SymbolTrace symbols, int part)
        {
            var values = new List<double>(symbols.SampleCount);

            for (int sample = 0; sample < symbols.SampleCount; sample++)
            {
                ConstellationPoint point = symbols.SampleAt(sample);

                values.Add(part == 0 ? point.I : point.Q);
            }

            return new ResultTraceData(
                trace,
                ResultTraceDomain.Sample,
                false,
                values,
                0.0,
                1.0,
                string.Empty,
                null,
                symbols.SamplesPerSymbol);
        }

        private static ResultTraceData Trellis(SymbolTrace symbols)
        {
            var values = new List<double>(symbols.SampleCount);

            for (int sample = 0; sample < symbols.SampleCount; sample++)
            {
                ConstellationPoint point = symbols.SampleAt(sample);

                values.Add(Math.Atan2(point.Q, point.I) * 180.0 / Math.PI);
            }

            return new ResultTraceData(
                ResultTrace.Trellis,
                ResultTraceDomain.Sample,
                false,
                values,
                0.0,
                1.0,
                "deg",
                null,
                symbols.SamplesPerSymbol);
        }

        private static ResultTraceData PerSymbol(
            ResultTrace trace, IList<double> values, string unit) =>
            new ResultTraceData(
                trace, ResultTraceDomain.Symbol, false, values, 0.0, 1.0, unit, null, 0.0);

        private static ResultTraceData Text(ResultTrace trace, IList<string> lines) =>
            new ResultTraceData(
                trace, ResultTraceDomain.Text, false, null, 0.0, 1.0, string.Empty, lines, 0.0);

        private static ResultTraceData Taps(DemodResult result)
        {
            var values = new List<double>(result.EqualiserCoefficients.Count * 2);

            foreach (ConstellationPoint tap in result.EqualiserCoefficients)
            {
                values.Add(tap.I);
                values.Add(tap.Q);
            }

            return new ResultTraceData(
                ResultTrace.EqualiserImpulseResponse,
                ResultTraceDomain.Sample,
                true,
                values,
                -(result.EqualiserCoefficients.Count / 2),
                1.0,
                string.Empty,
                null,
                result.Trace.SamplesPerSymbol);
        }

        /// <summary>
        /// The channel's estimated frequency response: what the equaliser had to undo.
        /// </summary>
        /// <param name="result">The demodulation.</param>
        /// <remarks>
        /// The equaliser is fitted to invert the channel, so the channel is the inverse of the
        /// equaliser's own response. Reported as magnitude and phase across the working bandwidth.
        /// <c>REQ-DEM-053</c> owns this trace properly, including the assertion that it matches an
        /// analytic two-ray response; what is here is the estimate, from the one thing that has
        /// measured the channel.
        /// </remarks>
        private static ResultTraceData Channel(DemodResult result)
        {
            int taps = result.EqualiserCoefficients.Count;
            int length = 16;

            while (length < taps * 8)
            {
                length *= 2;
            }

            var transform = new double[2 * length];

            for (int tap = 0; tap < taps; tap++)
            {
                transform[2 * tap] = result.EqualiserCoefficients[tap].I;
                transform[(2 * tap) + 1] = result.EqualiserCoefficients[tap].Q;
            }

            IFftProvider fft = FftProviders.Active;

            if (!fft.SupportsLength(length))
            {
                return new ResultTraceData(
                    ResultTrace.ChannelFrequencyResponse,
                    ResultTraceDomain.Frequency,
                    true,
                    new List<double>(),
                    0.0,
                    1.0,
                    string.Empty,
                    null,
                    0.0);
            }

            fft.Forward(new Span<double>(transform));

            var values = new List<double>(2 * length);

            for (int bin = 0; bin < length; bin++)
            {
                double i = transform[2 * bin];
                double q = transform[(2 * bin) + 1];
                double power = (i * i) + (q * q);

                // The channel is the equaliser inverted. A bin the equaliser drove to nothing is a
                // bin the channel had nothing in, and reporting an infinite channel gain there
                // would be reporting the reciprocal of a rounding error.
                if (power < 1e-18)
                {
                    values.Add(0.0);
                    values.Add(0.0);

                    continue;
                }

                values.Add(i / power);
                values.Add(-q / power);
            }

            double rate = result.Trace.SymbolRateHz * result.Trace.SamplesPerSymbol;

            return new ResultTraceData(
                ResultTrace.ChannelFrequencyResponse,
                ResultTraceDomain.Frequency,
                true,
                values,
                -rate / 2.0,
                rate / length,
                string.Empty,
                null,
                0.0);
        }

        private static ResultTraceData Spectrum(SymbolTrace symbols)
        {
            int count = symbols.SymbolCount;
            int length = 16;

            while (length < count)
            {
                length *= 2;
            }

            var transform = new double[2 * length];

            for (int symbol = 0; symbol < count; symbol++)
            {
                ConstellationPoint error = symbols.ErrorAt(symbol);

                transform[2 * symbol] = error.I;
                transform[(2 * symbol) + 1] = error.Q;
            }

            IFftProvider fft = FftProviders.Active;
            var values = new List<double>(length);

            if (fft.SupportsLength(length))
            {
                fft.Forward(new Span<double>(transform));

                // Centred, so a periodic impairment at a given rate appears at that rate on either
                // side of zero rather than wrapped to the end of the array.
                for (int bin = 0; bin < length; bin++)
                {
                    int from = (bin + (length / 2)) % length;
                    double i = transform[2 * from];
                    double q = transform[(2 * from) + 1];

                    values.Add(Math.Sqrt((i * i) + (q * q)) / count);
                }
            }

            return new ResultTraceData(
                ResultTrace.ErrorVectorSpectrum,
                ResultTraceDomain.Frequency,
                false,
                values,
                -symbols.SymbolRateHz / 2.0,
                symbols.SymbolRateHz / length,
                string.Empty,
                null,
                0.0);
        }

        private static List<double> Samples(SymbolTrace symbols)
        {
            var values = new List<double>(symbols.SampleCount * 2);

            for (int sample = 0; sample < symbols.SampleCount; sample++)
            {
                ConstellationPoint point = symbols.SampleAt(sample);

                values.Add(point.I);
                values.Add(point.Q);
            }

            return values;
        }

        private static List<double> Reference(DemodResult result)
        {
            ReadOnlySpan<float> reference = result.ReferenceWaveform;
            var values = new List<double>(reference.Length);

            for (int index = 0; index < reference.Length; index++)
            {
                values.Add(reference[index]);
            }

            return values;
        }

        private static List<double> ErrorVector(SymbolTrace symbols)
        {
            double reference = ReferencePower(symbols);
            var values = new List<double>(symbols.SymbolCount);

            for (int symbol = 0; symbol < symbols.SymbolCount; symbol++)
            {
                ConstellationPoint error = symbols.ErrorAt(symbol);

                values.Add(Math.Sqrt((error.I * error.I) + (error.Q * error.Q)) / reference * 100.0);
            }

            return values;
        }

        private static List<double> MagnitudeError(SymbolTrace symbols)
        {
            var values = new List<double>(symbols.SymbolCount);

            for (int symbol = 0; symbol < symbols.SymbolCount; symbol++)
            {
                ConstellationPoint measured = symbols.Measured[symbol];
                ConstellationPoint ideal = symbols.Ideal[symbol];

                double want = Math.Sqrt((ideal.I * ideal.I) + (ideal.Q * ideal.Q));
                double got = Math.Sqrt((measured.I * measured.I) + (measured.Q * measured.Q));

                values.Add(want < 1e-12 ? 0.0 : (got - want) / want * 100.0);
            }

            return values;
        }

        private static List<double> PhaseError(SymbolTrace symbols)
        {
            var values = new List<double>(symbols.SymbolCount);

            for (int symbol = 0; symbol < symbols.SymbolCount; symbol++)
            {
                ConstellationPoint measured = symbols.Measured[symbol];
                ConstellationPoint ideal = symbols.Ideal[symbol];

                double error =
                    Math.Atan2(measured.Q, measured.I) - Math.Atan2(ideal.Q, ideal.I);

                while (error > Math.PI)
                {
                    error -= 2.0 * Math.PI;
                }

                while (error < -Math.PI)
                {
                    error += 2.0 * Math.PI;
                }

                values.Add(error * 180.0 / Math.PI);
            }

            return values;
        }

        private static List<string> SymbolTableText(SymbolTrace symbols)
        {
            var values = new List<int>(symbols.Symbols);

            return new List<string>(
                Results.SymbolTable.Render(
                    values, symbols.BitsPerSymbol, SymbolTableFormat.Binary));
        }

        private static double ReferencePower(SymbolTrace symbols)
        {
            double sum = 0.0;

            foreach (ConstellationPoint ideal in symbols.Ideal)
            {
                sum += (ideal.I * ideal.I) + (ideal.Q * ideal.Q);
            }

            double rms = Math.Sqrt(sum / symbols.SymbolCount);

            return rms < 1e-12 ? 1.0 : rms;
        }
    }
}
