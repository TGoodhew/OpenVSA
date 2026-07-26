using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Measurement.Limits;

namespace OpenVSA.Measurement.Channels
{
    /// <summary>What a mask segment's limit is measured against.</summary>
    public enum MaskReference
    {
        /// <summary>An absolute level, in dBm.</summary>
        Absolute = 0,

        /// <summary>
        /// A level relative to the measured carrier power, in dBc.
        /// </summary>
        /// <remarks>
        /// How nearly every emission mask is written, because the limit is a statement about the
        /// transmitter's spectral regrowth rather than about its output power.
        /// </remarks>
        Relative,
    }

    /// <summary>
    /// One segment of a spectral emission mask: a band of offsets and the limit over it
    /// (<c>REQ-CHM-003</c>).
    /// </summary>
    /// <remarks>
    /// Limits may slope, because real masks do — a segment states a level at each of its two edges
    /// and the limit runs between them. A segment stating one level is simply one whose two edges
    /// are equal.
    /// </remarks>
    public sealed class EmissionMaskSegment
    {
        /// <summary>Creates a segment with a sloping limit.</summary>
        /// <param name="name">User-facing name; required, and it is what a failure is reported against.</param>
        /// <param name="startOffsetHz">Inner edge, as an offset from the carrier; must be non-negative.</param>
        /// <param name="stopOffsetHz">Outer edge; must be beyond the inner edge.</param>
        /// <param name="startLimit">Limit at the inner edge, in dBm or dBc.</param>
        /// <param name="stopLimit">Limit at the outer edge.</param>
        /// <param name="reference">Whether the limits are absolute or relative to the carrier.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is missing.</exception>
        /// <exception cref="ArgumentOutOfRangeException">An offset is out of range.</exception>
        public EmissionMaskSegment(
            string name,
            double startOffsetHz,
            double stopOffsetHz,
            double startLimit,
            double stopLimit,
            MaskReference reference = MaskReference.Relative)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException(
                    "A mask segment needs a name; it is what a failure is reported against.",
                    nameof(name));
            }

            if (!(startOffsetHz >= 0.0) || double.IsInfinity(startOffsetHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(startOffsetHz), startOffsetHz,
                    "A segment's inner edge is an offset from the carrier and cannot be negative; " +
                    "each segment is applied on both sides.");
            }

            if (!(stopOffsetHz > startOffsetHz) || double.IsInfinity(stopOffsetHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stopOffsetHz), stopOffsetHz,
                    "A segment's outer edge must lie beyond its inner edge.");
            }

            RequireFinite(startLimit, nameof(startLimit));
            RequireFinite(stopLimit, nameof(stopLimit));

            Name = name;
            StartOffsetHz = startOffsetHz;
            StopOffsetHz = stopOffsetHz;
            StartLimit = startLimit;
            StopLimit = stopLimit;
            Reference = reference;
        }

        /// <summary>Creates a segment with a flat limit.</summary>
        /// <param name="name">User-facing name.</param>
        /// <param name="startOffsetHz">Inner edge, as an offset from the carrier.</param>
        /// <param name="stopOffsetHz">Outer edge.</param>
        /// <param name="limit">Limit across the segment, in dBm or dBc.</param>
        /// <param name="reference">Whether the limit is absolute or relative to the carrier.</param>
        public EmissionMaskSegment(
            string name,
            double startOffsetHz,
            double stopOffsetHz,
            double limit,
            MaskReference reference = MaskReference.Relative)
            : this(name, startOffsetHz, stopOffsetHz, limit, limit, reference)
        {
        }

        /// <summary>User-facing name; what a failure names.</summary>
        public string Name { get; }

        /// <summary>Inner edge, as an offset from the carrier, in hertz.</summary>
        public double StartOffsetHz { get; }

        /// <summary>Outer edge, as an offset from the carrier, in hertz.</summary>
        public double StopOffsetHz { get; }

        /// <summary>Limit at the inner edge, in dBm or dBc.</summary>
        public double StartLimit { get; }

        /// <summary>Limit at the outer edge, in dBm or dBc.</summary>
        public double StopLimit { get; }

        /// <summary>Whether the limits are absolute or relative to the carrier.</summary>
        public MaskReference Reference { get; }

        /// <inheritdoc />
        public override string ToString() =>
            Name + ": " + (StartOffsetHz / 1e6).ToString("0.###", CultureInfo.CurrentCulture) +
            " to " + (StopOffsetHz / 1e6).ToString("0.###", CultureInfo.CurrentCulture) +
            " MHz at " + StartLimit.ToString("0.#", CultureInfo.CurrentCulture) +
            (StartLimit == StopLimit
                ? string.Empty
                : " to " + StopLimit.ToString("0.#", CultureInfo.CurrentCulture)) +
            (Reference == MaskReference.Relative ? " dBc" : " dBm");

        private static void RequireFinite(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(name, value, name + " must be finite.");
            }
        }
    }

    /// <summary>The outcome of an emission-mask measurement.</summary>
    public sealed class EmissionMaskResult
    {
        internal EmissionMaskResult(
            string name, double carrierPowerDbm, LimitTestResult limitResult)
        {
            Name = name;
            CarrierPowerDbm = carrierPowerDbm;
            LimitResult = limitResult;
        }

        /// <summary>The mask's name.</summary>
        public string Name { get; }

        /// <summary>The carrier power the relative segments were referenced to, in dBm.</summary>
        public double CarrierPowerDbm { get; }

        /// <summary>
        /// The limit engine's own result, one line per segment per side.
        /// </summary>
        /// <remarks>
        /// Exposed rather than translated. <c>REQ-CHM-003</c> requires this measurement to reuse
        /// <c>REQ-LIM-001</c>'s engine instead of reimplementing the comparison, and handing back
        /// the engine's own result type is what makes that impossible to quietly undo — a
        /// reimplementation would have to fabricate a <see cref="LimitTestResult"/> to keep this
        /// signature.
        /// </remarks>
        public LimitTestResult LimitResult { get; }

        /// <summary>Whether every segment passed.</summary>
        public bool Passed => LimitResult.Passed;

        /// <summary>Worst margin across every segment, in dB; negative where the mask is breached.</summary>
        public double WorstMarginDb => LimitResult.WorstMarginDb;

        /// <summary>
        /// The name of the segment with the worst margin, or <c>null</c> if nothing was tested.
        /// </summary>
        /// <remarks>
        /// <c>REQ-CHM-003</c>: "the reported failure naming the offending segment". Each segment is
        /// its own limit line for exactly this reason — a mask built as one line with gaps would
        /// evaluate identically and be able to report only that <em>the mask</em> failed.
        /// </remarks>
        public string OffendingSegment => LimitResult.Worst?.Line.Name;

        /// <summary>Frequency of the worst margin, in hertz.</summary>
        public double WorstHz => LimitResult.Worst?.WorstXHz ?? double.NaN;

        /// <inheritdoc />
        public override string ToString() =>
            Name + ": " + (Passed ? "PASS" : "FAIL") +
            (OffendingSegment == null
                ? " (nothing tested)"
                : ", worst " + WorstMarginDb.ToString("F2", CultureInfo.CurrentCulture) +
                  " dB in '" + OffendingSegment + "' at " +
                  WorstHz.ToString("G9", CultureInfo.CurrentCulture) + " Hz");
    }

    /// <summary>
    /// A spectral emission mask: named segments with their own limits, evaluated by the limit-test
    /// engine (<c>REQ-CHM-003</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This class builds a <see cref="LimitTest"/> and hands it the trace. It contains no
    /// comparison of its own.</strong> <c>REQ-CHM-003</c> asks for that explicitly, and the reason
    /// it asks is worth restating: a second implementation of "is this trace above or below that
    /// line" is where an inverted upper/lower comes back, in a place nobody thinks to look because
    /// the first implementation is well tested.
    /// </para>
    /// <para>
    /// <strong>Each segment becomes two limit lines, one per side.</strong> A mask applies either
    /// side of the carrier and the two sides fail independently. Building each as its own named
    /// line is also what lets a failure name the offending segment: one line with gaps in it would
    /// evaluate to exactly the same verdict and be able to report only that the mask failed.
    /// </para>
    /// <para>
    /// <strong>Relative segments are resolved against a measured carrier power, once.</strong> The
    /// limit lines handed to the engine are in dBm, because the engine compares dBm — converting
    /// inside the comparison would mean the engine knowing about dBc, and the mask is the only
    /// thing that should.
    /// </para>
    /// </remarks>
    public sealed class EmissionMask
    {
        private readonly List<EmissionMaskSegment> _segments = new List<EmissionMaskSegment>();

        /// <summary>Creates a mask.</summary>
        /// <param name="name">User-facing name; required.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is missing.</exception>
        public EmissionMask(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("A mask needs a name.", nameof(name));
            }

            Name = name;
        }

        /// <summary>User-facing name.</summary>
        public string Name { get; }

        /// <summary>
        /// Margin in dB applied to every segment, on the passing side.
        /// </summary>
        /// <remarks>
        /// Passed through to the limit lines, so it means exactly what <c>REQ-LIM-001</c> says it
        /// means: a margin tightens the test rather than loosening it.
        /// </remarks>
        public double MarginDb { get; set; }

        /// <summary>The segments, in the order they were added.</summary>
        public IReadOnlyList<EmissionMaskSegment> Segments =>
            new ReadOnlyCollection<EmissionMaskSegment>(_segments);

        /// <summary>Adds a segment.</summary>
        /// <param name="segment">The segment.</param>
        /// <returns>This mask, so segments can be chained.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="segment"/> is null.</exception>
        public EmissionMask Add(EmissionMaskSegment segment)
        {
            if (segment == null)
            {
                throw new ArgumentNullException(nameof(segment));
            }

            _segments.Add(segment);
            return this;
        }

        /// <summary>
        /// Builds the limit test this mask evaluates through.
        /// </summary>
        /// <param name="carrierCentreHz">Absolute centre frequency of the carrier, in hertz.</param>
        /// <param name="carrierPowerDbm">
        /// Measured carrier power, which the relative segments are referenced to.
        /// </param>
        /// <returns>A test with one named line per segment per side.</returns>
        /// <exception cref="ArgumentOutOfRangeException">A value is not finite.</exception>
        /// <remarks>
        /// Public because it is the shared code path <c>REQ-CHM-003</c> asks to be able to assert,
        /// and because a caller wanting to draw the mask on screen needs the same lines the
        /// measurement used rather than a second rendering of the same intent.
        /// </remarks>
        public LimitTest ToLimitTest(double carrierCentreHz, double carrierPowerDbm)
        {
            if (double.IsNaN(carrierCentreHz) || double.IsInfinity(carrierCentreHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(carrierCentreHz), carrierCentreHz, "A centre frequency must be finite.");
            }

            if (double.IsNaN(carrierPowerDbm) || double.IsInfinity(carrierPowerDbm))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(carrierPowerDbm), carrierPowerDbm,
                    "A carrier power must be finite; a mask with relative segments has nothing to " +
                    "reference without it.");
            }

            var test = new LimitTest(Name);

            foreach (EmissionMaskSegment segment in _segments)
            {
                double startDbm = Resolve(segment.StartLimit, segment.Reference, carrierPowerDbm);
                double stopDbm = Resolve(segment.StopLimit, segment.Reference, carrierPowerDbm);

                // Lower side: the inner edge is the higher frequency, so the points run outward
                // downwards in frequency. Order does not matter to the engine, which spans a
                // segment either way round, but it keeps the line readable.
                var lower = new LimitLine(segment.Name + " lower", LimitSide.Upper)
                {
                    MarginDb = MarginDb,
                };

                lower.Add(carrierCentreHz - segment.StopOffsetHz, stopDbm);
                lower.Add(carrierCentreHz - segment.StartOffsetHz, startDbm);

                var upper = new LimitLine(segment.Name + " upper", LimitSide.Upper)
                {
                    MarginDb = MarginDb,
                };

                upper.Add(carrierCentreHz + segment.StartOffsetHz, startDbm);
                upper.Add(carrierCentreHz + segment.StopOffsetHz, stopDbm);

                test.Add(lower).Add(upper);
            }

            return test;
        }

        /// <summary>
        /// Evaluates a spectrum against this mask.
        /// </summary>
        /// <param name="frame">The spectrum.</param>
        /// <param name="carrierCentreHz">Absolute centre frequency of the carrier, in hertz.</param>
        /// <param name="carrierPowerDbm">Measured carrier power, in dBm.</param>
        /// <returns>Pass or fail, with the offending segment named.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A value is not finite.</exception>
        public EmissionMaskResult Evaluate(
            SpectrumFrame frame, double carrierCentreHz, double carrierPowerDbm)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            LimitTest test = ToLimitTest(carrierCentreHz, carrierPowerDbm);

            return new EmissionMaskResult(Name, carrierPowerDbm, test.Evaluate(frame));
        }

        /// <summary>
        /// Evaluates against a carrier whose power is measured from the same trace.
        /// </summary>
        /// <param name="frame">The spectrum.</param>
        /// <param name="carrier">The carrier channel, integrated to give the reference power.</param>
        /// <param name="carrierCentreHz">Absolute centre frequency of the carrier, in hertz.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <remarks>
        /// The usual case, and the one worth making easy: a mask's relative limits refer to the
        /// carrier power of the very signal being tested, so measuring it separately and passing it
        /// in is an opportunity for the two to be of different things.
        /// </remarks>
        public EmissionMaskResult Evaluate(
            SpectrumFrame frame, ChannelDefinition carrier, double carrierCentreHz)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (carrier == null)
            {
                throw new ArgumentNullException(nameof(carrier));
            }

            AcpResult power = new AcpMeasurement(carrier).Measure(frame, carrierCentreHz);

            return Evaluate(frame, carrierCentreHz, power.Carrier.AbsoluteDbm);
        }

        private static double Resolve(
            double limit, MaskReference reference, double carrierPowerDbm) =>
            reference == MaskReference.Relative ? carrierPowerDbm + limit : limit;
    }
}
