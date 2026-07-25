using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenVSA.Core;

namespace OpenVSA.Hal
{
    /// <summary>
    /// One parameter the front end could not honour as requested, with the reason.
    /// </summary>
    /// <remarks>
    /// <c>REQ-HAL-001</c> forbids silently altering a user's request. A coercion is therefore a
    /// first-class, inspectable record rather than a log line: the UI surfaces it, and
    /// <c>REQ-ARC-002</c> requires one event-log entry per coercion when a front end changes.
    /// </remarks>
    public sealed class ParameterCoercion
    {
        /// <summary>Creates a coercion record.</summary>
        /// <param name="parameter">Name of the coerced parameter, as the user knows it.</param>
        /// <param name="requested">The value asked for.</param>
        /// <param name="honoured">The value the front end will actually use.</param>
        /// <param name="reason">Why the request could not be honoured, in user-facing terms.</param>
        public ParameterCoercion(string parameter, double requested, double honoured, string reason)
        {
            if (string.IsNullOrEmpty(parameter))
            {
                throw new ArgumentException("Parameter name is required.", nameof(parameter));
            }

            if (string.IsNullOrEmpty(reason))
            {
                // A coercion without a reason is precisely the silent alteration REQ-HAL-001
                // prohibits, so it is rejected at construction rather than tolerated.
                throw new ArgumentException("A coercion must state its reason.", nameof(reason));
            }

            Parameter = parameter;
            Requested = requested;
            Honoured = honoured;
            Reason = reason;
        }

        /// <summary>Name of the coerced parameter.</summary>
        public string Parameter { get; }

        /// <summary>The value asked for.</summary>
        public double Requested { get; }

        /// <summary>The value the front end will use.</summary>
        public double Honoured { get; }

        /// <summary>Why the request could not be honoured.</summary>
        public string Reason { get; }

        /// <inheritdoc />
        public override string ToString() => string.Format(
            CultureInfo.InvariantCulture,
            "{0}: requested {1}, honoured {2} ({3})", Parameter, Requested, Honoured, Reason);
    }

    /// <summary>
    /// What a front end will actually do, given an <see cref="AcquisitionRequest"/>.
    /// </summary>
    /// <remarks>
    /// Produced by <see cref="IFrontEnd.Negotiate"/>, which <c>REQ-HAL-001</c> requires to be a
    /// pure function: producing a plan sends nothing to the instrument and changes no state, so a
    /// caller may negotiate freely to discover what is possible before committing.
    /// </remarks>
    public sealed class AcquisitionPlan
    {
        /// <summary>Creates a plan.</summary>
        /// <param name="centerFrequencyHz">Honoured centre frequency, in hertz.</param>
        /// <param name="spanHz">Honoured span, in hertz.</param>
        /// <param name="sampleRateHz">Honoured sample rate, in hertz.</param>
        /// <param name="samplesPerBlock">Honoured block size, in complex samples.</param>
        /// <param name="referenceLevelDbm">Honoured reference level, in dBm.</param>
        /// <param name="supportsGapFreeStreaming">
        /// Whether this plan can be acquired gap-free. Computed per plan from measured
        /// throughput, never hard-coded per front end — see <c>REQ-NFR-027</c>.
        /// </param>
        /// <param name="coercions">Parameters that could not be honoured as requested.</param>
        /// <param name="path">
        /// The acquisition path that will be used, which may differ from the one requested — a
        /// front end with no baseband capability coerces to <see cref="AnalysisPath.ComplexZoom"/>
        /// and says so.
        /// </param>
        public AcquisitionPlan(
            double centerFrequencyHz,
            double spanHz,
            double sampleRateHz,
            int samplesPerBlock,
            double referenceLevelDbm,
            bool supportsGapFreeStreaming,
            IEnumerable<ParameterCoercion> coercions,
            AnalysisPath path = AnalysisPath.ComplexZoom)
        {
            CenterFrequencyHz = centerFrequencyHz;
            SpanHz = spanHz;
            SampleRateHz = sampleRateHz;
            SamplesPerBlock = samplesPerBlock;
            ReferenceLevelDbm = referenceLevelDbm;
            SupportsGapFreeStreaming = supportsGapFreeStreaming;
            Coercions = (coercions ?? Enumerable.Empty<ParameterCoercion>()).ToList().AsReadOnly();
            Path = path;
        }

        /// <summary>The acquisition path that will be used (<c>REQ-ACQ-001</c>).</summary>
        public AnalysisPath Path { get; }

        /// <summary>Honoured centre frequency, in hertz.</summary>
        public double CenterFrequencyHz { get; }

        /// <summary>Honoured span, in hertz.</summary>
        public double SpanHz { get; }

        /// <summary>Honoured sample rate, in hertz.</summary>
        public double SampleRateHz { get; }

        /// <summary>Honoured block size, in complex samples.</summary>
        public int SamplesPerBlock { get; }

        /// <summary>Honoured reference level, in dBm.</summary>
        public double ReferenceLevelDbm { get; }

        /// <summary>
        /// Whether this plan can be acquired gap-free, computed per plan (<c>REQ-NFR-027</c>).
        /// </summary>
        public bool SupportsGapFreeStreaming { get; }

        /// <summary>Parameters that could not be honoured as requested. Never <c>null</c>.</summary>
        public IReadOnlyList<ParameterCoercion> Coercions { get; }

        /// <summary>Whether any parameter was coerced.</summary>
        public bool Coerced => Coercions.Count > 0;

        /// <summary>Finds the coercion for a named parameter, or <c>null</c> if it was honoured.</summary>
        /// <param name="parameter">Parameter name, compared ordinally.</param>
        public ParameterCoercion CoercionFor(string parameter) =>
            Coercions.FirstOrDefault(c => string.Equals(c.Parameter, parameter, StringComparison.Ordinal));
    }
}
