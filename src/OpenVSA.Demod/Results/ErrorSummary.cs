using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace OpenVSA.Demod.Results
{
    /// <summary>
    /// One row of the error summary (<c>REQ-UI-053</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A row is a label, a value with a unit, and — for the metrics that have one — a peak and the
    /// symbol it occurred at. <strong>A scalar-only metric renders one value and omits the peak
    /// columns rather than padding them with zeros</strong>, which is the requirement's own
    /// wording and the difference between a summary that reads and one that has to be decoded.
    /// </para>
    /// </remarks>
    public sealed class ErrorMetric
    {
        /// <summary>Creates a metric with an RMS value, a peak, and the symbol the peak fell on.</summary>
        /// <param name="label">The row label; one of <see cref="ErrorSummary.Labels"/>.</param>
        /// <param name="unit">The unit the values are in, before any engineering prefix.</param>
        /// <param name="rms">The RMS value.</param>
        /// <param name="peak">The peak value.</param>
        /// <param name="peakSymbol">Which symbol the peak fell on.</param>
        public ErrorMetric(string label, string unit, double rms, double peak, int peakSymbol)
            : this(label, unit, rms)
        {
            Peak = peak;
            PeakSymbol = peakSymbol;
            HasPeak = true;
        }

        /// <summary>Creates a scalar metric: one value, no peak and no symbol.</summary>
        /// <param name="label">The row label.</param>
        /// <param name="unit">The unit.</param>
        /// <param name="value">The value.</param>
        /// <exception cref="ArgumentException"><paramref name="label"/> is null or blank.</exception>
        public ErrorMetric(string label, string unit, double value)
        {
            if (string.IsNullOrEmpty(label) || label.Trim().Length == 0)
            {
                throw new ArgumentException("A metric needs a label.", nameof(label));
            }

            Label = label.Trim();
            Unit = unit ?? string.Empty;
            Rms = value;
        }

        /// <summary>The row label, in the reference product's terse style.</summary>
        public string Label { get; }

        /// <summary>The unit, before any engineering prefix.</summary>
        public string Unit { get; }

        /// <summary>The RMS value, or the single value of a scalar metric.</summary>
        public double Rms { get; }

        /// <summary>The peak value, when there is one.</summary>
        public double Peak { get; }

        /// <summary>Which symbol the peak fell on, when there is one.</summary>
        public int PeakSymbol { get; }

        /// <summary>Whether this metric has a peak and a symbol as well as an RMS value.</summary>
        public bool HasPeak { get; }

        /// <inheritdoc />
        public override string ToString() => Label + " = " + Rms + " " + Unit;
    }

    /// <summary>
    /// The error summary of <c>REQ-UI-053</c>: the metrics, and the layout they are shown in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The requirement gives the actual on-screen text of a real analyser of this family and makes
    /// it the layout model. What is testable in it, and what <see cref="Render"/> reproduces:
    /// </para>
    /// <list type="bullet">
    /// <item><description>the <c>=</c> at a fixed column across every row;</description></item>
    /// <item><description>RMS, then peak, then "at symbol N";</description></item>
    /// <item><description>engineering prefixes on units — <c>m%rms</c>, <c>mdeg</c> — rather than
    /// exponent notation;</description></item>
    /// <item><description>the terse labels exactly: <c>Carr Ofst</c>, never "Carrier Offset".</description></item>
    /// </list>
    /// <para>
    /// <strong>The <c>=</c> column is the whole reason this needs a fixed-width slot.</strong> The
    /// requirement's own model has <c>Phase Error=</c> with no space before the sign — the label
    /// field is a fixed width and a long label runs right up to it. Rendering this in a
    /// proportional face puts the equals signs in a ragged line and the numbers nowhere near each
    /// other, which is why <c>REQ-UI-052</c> puts both portions in the Tabular slot.
    /// </para>
    /// </remarks>
    public sealed class ErrorSummary
    {
        /// <summary>
        /// The column the <c>=</c> sits in, counted from zero.
        /// </summary>
        /// <remarks>
        /// Eleven, which is the width of the longest label in <see cref="Labels"/> plus one — the
        /// requirement's model shows <c>Phase Error=</c> running straight into the sign, so the
        /// field is exactly wide enough for the longest label and no wider.
        /// </remarks>
        public const int EqualsColumn = 11;

        /// <summary>
        /// The row labels of <c>REQ-UI-053</c>, exactly as it lists them.
        /// </summary>
        /// <remarks>
        /// <strong>Asserted as literals by a test, and that is the point of them being here.</strong>
        /// The house style is short, truncated and no-space-where-possible; the natural instinct is
        /// to write "Carrier Offset" and "Symbol Clock Error", and a summary that did would not be
        /// the display this requirement describes.
        /// </remarks>
        public static readonly IReadOnlyList<string> Labels =
            new ReadOnlyCollection<string>(new List<string>
            {
                "Amp Droop",
                "Carr Ofst",
                "EVM",
                "EVM Pk",
                "Freq Err",
                "Mag Err",
                "Offset EVM",
                "Phase Err",
                "Pilot Lvl",
                "Time Offset",
                "IQ Offset",
                "IQ Gain Imbalance",
                "IQ Quad. Error",
                "IQ Timing Skew",
                "SymClk Err",
                "RSSI",
            });

        private readonly List<ErrorMetric> _metrics = new List<ErrorMetric>();

        /// <summary>The metrics, in the order they will be rendered.</summary>
        public IReadOnlyList<ErrorMetric> Metrics => _metrics;

        /// <summary>
        /// Adds a metric.
        /// </summary>
        /// <param name="metric">The metric.</param>
        /// <exception cref="ArgumentNullException"><paramref name="metric"/> is null.</exception>
        /// <returns>This summary, so metrics can be chained.</returns>
        public ErrorSummary Add(ErrorMetric metric)
        {
            if (metric == null)
            {
                throw new ArgumentNullException(nameof(metric));
            }

            _metrics.Add(metric);
            return this;
        }

        /// <summary>
        /// Adds a metric, or replaces the one already under that label.
        /// </summary>
        /// <param name="metric">The metric.</param>
        /// <returns>This summary.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="metric"/> is null.</exception>
        /// <remarks>
        /// For the metrics a later step knows better than an earlier one: <c>REQ-DEM-062</c>'s
        /// Offset EVM is computed from the same points as EVM and displaces it, and appending
        /// instead would leave two rows under one label for the table to choose between.
        /// </remarks>
        public ErrorSummary Replace(ErrorMetric metric)
        {
            if (metric == null)
            {
                throw new ArgumentNullException(nameof(metric));
            }

            for (int at = 0; at < _metrics.Count; at++)
            {
                if (string.Equals(_metrics[at].Label, metric.Label, StringComparison.Ordinal))
                {
                    _metrics[at] = metric;

                    return this;
                }
            }

            _metrics.Add(metric);

            return this;
        }

        /// <summary>
        /// Computes the summary a result implies (<c>REQ-DEM-070</c>'s metrics over
        /// <c>REQ-UI-053</c>'s layout).
        /// </summary>
        /// <param name="trace">The demodulated result.</param>
        /// <exception cref="ArgumentNullException"><paramref name="trace"/> is null.</exception>
        /// <remarks>
        /// <para>
        /// The four the geometry alone can support — EVM, magnitude error, phase error and IQ
        /// offset — and no more. A summary that invented a frequency error or a symbol-clock error
        /// from a result that cannot show one would be the failure <c>REQ-DEM-072</c> exists to
        /// prevent: a number without its provenance. The rest arrive with the demodulator that can
        /// measure them.
        /// </para>
        /// <para>
        /// EVM is referenced to the RMS of the ideal points rather than to unity, so a result whose
        /// constellation is not normalised still gives the figure an instrument would.
        /// </para>
        /// </remarks>
        public static ErrorSummary For(SymbolTrace trace)
        {
            if (trace == null)
            {
                throw new ArgumentNullException(nameof(trace));
            }

            return For(trace.Measured, trace.Ideal);
        }

        /// <summary>The normalisation the metrics in this summary were referenced to.</summary>
        /// <remarks>
        /// <para>
        /// <c>REQ-DEM-072</c>: a percentage whose denominator is unstated is a number without its
        /// provenance, and <c>REQ-DEM-061</c> makes that denominator a setting. This is what it was
        /// on the measurement these metrics came from, and <see cref="EvmReference.Describe"/> is
        /// the sentence to put beside them.
        /// </para>
        /// <para>
        /// Null on a summary assembled by hand from metrics rather than computed from points, and
        /// on an empty one.
        /// </para>
        /// </remarks>
        public EvmReference Reference { get; private set; }

        /// <summary>
        /// Computes the same summary from the points alone, before there is a trace to hold them.
        /// </summary>
        /// <param name="measured">The measured point for each symbol.</param>
        /// <param name="ideal">The ideal point for each symbol, in the same order.</param>
        /// <exception cref="ArgumentNullException">A list is null.</exception>
        /// <exception cref="ArgumentException">The lists are different lengths.</exception>
        /// <remarks>
        /// The demodulation chain of <c>REQ-DEM-001</c> computes its error metrics at step 13 and
        /// generates its result traces at step 14, in that order, so at the moment the metrics are
        /// wanted there is no <see cref="SymbolTrace"/> yet. Rather than compute EVM twice — once
        /// here and once in whatever step 13 grew of its own — <see cref="For(SymbolTrace)"/>
        /// delegates to this, and there is one implementation of the four metrics the geometry
        /// supports.
        /// </remarks>
        public static ErrorSummary For(
            IReadOnlyList<ConstellationPoint> measured, IReadOnlyList<ConstellationPoint> ideal)
        {
            return For(measured, ideal, null);
        }

        /// <summary>
        /// The same summary, referenced to a normalisation the caller chose
        /// (<c>REQ-DEM-061</c>).
        /// </summary>
        /// <param name="measured">The measured point for each symbol.</param>
        /// <param name="ideal">The ideal point for each symbol, in the same order.</param>
        /// <param name="reference">
        /// What to divide the errors by, or <c>null</c> to take the RMS of the ideal points — which
        /// is the default of <c>REQ-DEM-061</c> and what every caller got before it was a choice.
        /// </param>
        /// <returns>The summary.</returns>
        /// <exception cref="ArgumentNullException">A list is null.</exception>
        /// <exception cref="ArgumentException">The lists are different lengths.</exception>
        /// <remarks>
        /// <strong>Pass a reference built from the format, not from these points, when there is
        /// one.</strong> <c>REQ-DEM-061</c> normalises to "the reference constellation", and a
        /// divisor computed from the symbols that happened to be decided makes a short window of
        /// 64-QAM read differently from one acquisition to the next. The chain has the
        /// constellation and passes it; the callers that hold points and no format get the
        /// convergent approximation, which is what the <c>null</c> is.
        /// </remarks>
        public static ErrorSummary For(
            IReadOnlyList<ConstellationPoint> measured,
            IReadOnlyList<ConstellationPoint> ideal,
            EvmReference reference)
        {
            if (measured == null)
            {
                throw new ArgumentNullException(nameof(measured));
            }

            if (ideal == null)
            {
                throw new ArgumentNullException(nameof(ideal));
            }

            if (measured.Count != ideal.Count)
            {
                throw new ArgumentException(
                    "There are " + measured.Count + " measured points and " + ideal.Count +
                    " ideal ones. A metric is a comparison, so they come in pairs.",
                    nameof(measured));
            }

            var summary = new ErrorSummary();

            if (measured.Count == 0)
            {
                return summary;
            }

            EvmReference norm = reference ??
                EvmReference.FromPoints(EvmNormalisation.RmsMagnitude, ideal, 0.0);

            summary.Reference = norm;

            double volts = norm.Volts;

            double errorSquared = 0.0;
            double magSquared = 0.0;
            double phaseSquared = 0.0;

            double worstError = 0.0;
            double worstMag = 0.0;
            double worstPhase = 0.0;

            int worstErrorAt = 0;
            int worstMagAt = 0;
            int worstPhaseAt = 0;

            double offsetI = 0.0;
            double offsetQ = 0.0;

            for (int symbol = 0; symbol < measured.Count; symbol++)
            {
                ConstellationPoint point = measured[symbol];
                ConstellationPoint idealPoint = ideal[symbol];
                var error = new ConstellationPoint(
                    point.I - idealPoint.I, point.Q - idealPoint.Q);

                double magnitude = Math.Sqrt(error.I * error.I + error.Q * error.Q) / volts;

                errorSquared += magnitude * magnitude;

                if (magnitude > Math.Abs(worstError))
                {
                    worstError = magnitude;
                    worstErrorAt = symbol;
                }

                double idealMagnitude = Math.Sqrt(
                    (idealPoint.I * idealPoint.I) +
                    (idealPoint.Q * idealPoint.Q));

                double measuredMagnitude = Math.Sqrt((point.I * point.I) + (point.Q * point.Q));

                // REQ-DEM-063 divides by V_norm, NOT by this symbol's own ideal magnitude. The
                // difference is invisible on a constant-modulus format and large on a QAM, where a
                // per-symbol ratio weights an error on an inner point far more heavily than the same
                // error on an outer one -- and would report a magnitude error that depended on which
                // symbols were sent. An earlier form of this did exactly that.
                double magError = (measuredMagnitude - idealMagnitude) / volts;

                magSquared += magError * magError;

                if (Math.Abs(magError) > Math.Abs(worstMag))
                {
                    worstMag = magError;
                    worstMagAt = symbol;
                }

                // REQ-DEM-064's arg(z r*), and it is written as the argument of a product rather
                // than as a difference of two arguments on purpose: the product's argument is
                // ALREADY the principal value in (-pi, pi], so a symbol whose error approaches +/-pi
                // lands on the right branch without a wrap step to get wrong. The 180/pi is the
                // requirement's own emphasis -- the bare expression is radians and the reported
                // quantity is degrees.
                var product = new ConstellationPoint(
                    (point.I * idealPoint.I) + (point.Q * idealPoint.Q),
                    (point.Q * idealPoint.I) - (point.I * idealPoint.Q));

                double phaseError = Math.Atan2(product.Q, product.I) * 180.0 / Math.PI;

                phaseSquared += phaseError * phaseError;

                if (Math.Abs(phaseError) > Math.Abs(worstPhase))
                {
                    worstPhase = phaseError;
                    worstPhaseAt = symbol;
                }

                offsetI += error.I;
                offsetQ += error.Q;
            }

            int count = measured.Count;

            summary.Add(new ErrorMetric(
                "EVM", "%rms", Math.Sqrt(errorSquared / count) * 100.0, worstError * 100.0, worstErrorAt));

            summary.Add(new ErrorMetric(
                "Mag Err", "%rms", Math.Sqrt(magSquared / count) * 100.0, worstMag * 100.0, worstMagAt));

            summary.Add(new ErrorMetric(
                "Phase Err", "deg", Math.Sqrt(phaseSquared / count), worstPhase, worstPhaseAt));

            // The mean error vector as a level below the reference: a constellation whose centre of
            // gravity is off the origin has a carrier leaking through, and that is what this reads.
            double offset = Math.Sqrt(
                (offsetI / count) * (offsetI / count) + (offsetQ / count) * (offsetQ / count)) /
                volts;

            summary.Add(new ErrorMetric(
                "IQ Offset", "dB", offset < 1e-12 ? -200.0 : 20.0 * Math.Log10(offset)));

            return summary;
        }

        /// <summary>
        /// This summary as the error summary table a format shows (<c>REQ-DEM-071</c>).
        /// </summary>
        /// <param name="family">The format's family.</param>
        /// <param name="isOffset">Whether the format staggers I and Q by half a symbol.</param>
        /// <returns>
        /// A summary holding every metric that applies to the format, in the table's order, with
        /// this summary's values where it has them.
        /// </returns>
        /// <remarks>
        /// <para>
        /// <strong>Applicable but not computed shows <c>NAN</c>, not nothing and not a stale
        /// value.</strong> That is <c>REQ-DEM-071</c>'s own instruction, and it matters most in the
        /// case it names: changing format must not leave the previous format's number under a row
        /// the new format happens to share. A row that is applicable is always present; whether
        /// there is a number in it is a separate question, and the display says which.
        /// </para>
        /// <para>
        /// The metrics this build computes are the four the geometry supports. The rest are
        /// applicable and unmeasured, and read <c>NAN</c> until the requirements that specify them
        /// arrive — <c>REQ-DEM-065</c>'s frequency error, <c>REQ-DEM-069</c>'s SNR and the others of
        /// section 11.7. The table's shape is right now, which is what lets those land one at a time
        /// without the display changing shape under the user each time.
        /// </para>
        /// </remarks>
        public ErrorSummary AsTableFor(ModulationFamily family, bool isOffset)
        {
            var table = new ErrorSummary { Reference = Reference };

            foreach (string label in MetricApplicability.LabelsFor(family, isOffset))
            {
                ErrorMetric computed = null;

                foreach (ErrorMetric metric in _metrics)
                {
                    if (string.Equals(metric.Label, label, StringComparison.Ordinal))
                    {
                        computed = metric;

                        break;
                    }
                }

                table.Add(
                    computed ??
                    new ErrorMetric(label, MetricApplicability.UnitOf(label), double.NaN));
            }

            return table;
        }

        /// <summary>
        /// The summary as <c>REQ-UI-053</c>'s layout, one string per row.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The label is left-justified in a field of <see cref="EqualsColumn"/> characters, so the
        /// <c>=</c> lands in the same column on every row whatever the label's length — including
        /// the case the requirement's own model shows, where a label fills the field and the sign
        /// abuts it.
        /// </para>
        /// <para>
        /// Values carry engineering prefixes: <c>248.7475 m%rms</c>, not <c>2.487475E-01 %rms</c>.
        /// The prefix goes on the unit rather than being written as a separate factor, which is
        /// what makes <c>m%rms</c> and <c>mdeg</c> read as the requirement writes them.
        /// </para>
        /// </remarks>
        public IReadOnlyList<string> Render()
        {
            var rows = new List<string>(_metrics.Count);

            foreach (ErrorMetric metric in _metrics)
            {
                var row = new StringBuilder();

                row.Append(metric.Label.PadRight(EqualsColumn));
                row.Append('=');
                row.Append(' ');
                row.Append(Engineering(metric.Rms, metric.Unit).PadLeft(16));

                if (metric.HasPeak)
                {
                    row.Append(' ');
                    row.Append(Engineering(metric.Peak, PeakUnit(metric.Unit)).PadLeft(18));
                    row.Append(" at symbol ");
                    row.Append(metric.PeakSymbol.ToString(CultureInfo.InvariantCulture));
                }

                rows.Add(row.ToString().TrimEnd());
            }

            return rows;
        }

        /// <summary>
        /// A value with an engineering prefix on its unit (<c>REQ-UI-053</c>).
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="unit">The unit the value is in.</param>
        /// <remarks>
        /// <para>
        /// Seven significant figures, which is what the requirement's model shows —
        /// <c>248.7475 m%rms</c>, <c>1.043872 deg pk</c> — and enough that the last digit of an EVM
        /// figure is below the noise of any real measurement rather than above it.
        /// </para>
        /// <para>
        /// Decibels take no prefix. A level is already logarithmic, and <c>-67.543 dB</c> written
        /// as <c>-67.543 dB</c> is what every instrument shows; <c>-67.543 </c> with a prefix would
        /// be a unit nobody uses.
        /// </para>
        /// </remarks>
        public static string Engineering(double value, string unit)
        {
            if (double.IsNaN(value))
            {
                return "NAN " + unit;
            }

            if (unit.StartsWith("dB", StringComparison.Ordinal) || value == 0.0)
            {
                return Significant(value) + " " + unit;
            }

            string[] prefixes = { "p", "n", "u", "m", string.Empty, "k", "M", "G" };
            const int Unity = 4;

            double magnitude = Math.Abs(value);
            int step = Unity;

            while (magnitude < 1.0 && step > 0)
            {
                magnitude *= 1000.0;
                value *= 1000.0;
                step--;
            }

            while (magnitude >= 1000.0 && step < prefixes.Length - 1)
            {
                magnitude /= 1000.0;
                value /= 1000.0;
                step++;
            }

            return Significant(value) + " " + prefixes[step] + unit;
        }

        /// <summary>
        /// The unit a peak is shown in.
        /// </summary>
        /// <remarks>
        /// The RMS suffix comes off, because a peak is not an RMS value. The requirement's own
        /// model shows exactly this — <c>248.7475 m%rms</c> against <c>732.2379 m% pk</c> on the
        /// same row — and carrying <c>rms</c> into the peak column would label the largest single
        /// error as an average.
        /// </remarks>
        private static string PeakUnit(string unit)
        {
            string bare = unit.EndsWith("rms", StringComparison.Ordinal)
                ? unit.Substring(0, unit.Length - 3)
                : unit;

            return bare + " pk";
        }

        private static string Significant(double value) =>
            value.ToString("G7", CultureInfo.InvariantCulture);

        /// <inheritdoc />
        public override string ToString() => _metrics.Count + " metric(s)";
    }
}
